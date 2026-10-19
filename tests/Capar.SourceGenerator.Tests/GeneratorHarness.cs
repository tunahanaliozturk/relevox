using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Capar.SourceGenerator.Tests;

/// <summary>What one generator run produced.</summary>
/// <param name="Diagnostics">Everything the generator reported.</param>
/// <param name="Sources">Emitted files, by hint name.</param>
/// <param name="CompilationErrors">Errors from compiling the result, so output that does not build cannot pass.</param>
/// <param name="Compilation">The compilation after generation, for tests that go on to run it.</param>
public sealed record GeneratorRun(
    ImmutableArray<Diagnostic> Diagnostics,
    IReadOnlyDictionary<string, string> Sources,
    ImmutableArray<Diagnostic> CompilationErrors,
    Compilation Compilation)
{
    public string Mediator => Sources["CaparMediator.g.cs"];

    public IEnumerable<string> Ids => Diagnostics.Select(diagnostic => diagnostic.Id);
}

/// <summary>
/// Runs the generator in process over source text.
/// </summary>
/// <remarks>
/// The output is compiled as well as inspected. A generator test that only looks at the text it produced will
/// pass happily while emitting code that does not build, and that is the failure that reaches a user.
/// </remarks>
public static class GeneratorHarness
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);
    internal static readonly ImmutableArray<MetadataReference> References = BuildReferences();

    public static GeneratorRun Run(params string[] sources) => Run("Subject", [], sources);

    public static GeneratorRun Run(string assemblyName, IEnumerable<MetadataReference> extra, params string[] sources) =>
        Run(assemblyName, extra, strict: false, sources);

    /// <summary>
    /// Runs the generator the way a strict consumer builds: every warning an error, documentation comments
    /// checked. Generated code that is fine by default but warns in such a build would break that build.
    /// </summary>
    public static GeneratorRun RunStrict(params string[] sources) => Run("Subject", [], strict: true, sources);

    public static GeneratorRun Run(string assemblyName, IEnumerable<MetadataReference> extra, bool strict, params string[] sources) =>
        Run(assemblyName, extra, strict, [], sources);

    /// <summary>Like <see cref="Run(string, IEnumerable{MetadataReference}, string[])"/>, with another generator running alongside.</summary>
    public static GeneratorRun RunAlongside(IIncrementalGenerator other, string assemblyName, IEnumerable<MetadataReference> extra, params string[] sources) =>
        Run(assemblyName, extra, strict: false, [other], sources);

    private static GeneratorRun Run(string assemblyName, IEnumerable<MetadataReference> extra, bool strict, IIncrementalGenerator[] others, string[] sources)
    {
        CSharpCompilation compilation = Compile(assemblyName, extra, sources, strict);

        GeneratorDriver driver = CSharpGeneratorDriver
            .Create(
                [new CaparGenerator().AsSourceGenerator(), .. others.Select(static other => other.AsSourceGenerator())],
                parseOptions: strict ? ParseOptions.WithDocumentationMode(DocumentationMode.Diagnose) : ParseOptions)
            .RunGeneratorsAndUpdateCompilation(compilation, out Compilation output, out _);

        GeneratorDriverRunResult result = driver.GetRunResult();

        // The rules are reported by the analyzer, against the user's own source, so that is where they are read.
        // It runs over the compilation the generator produced, as in a real build: an AddCapar call only binds to
        // this project's method once that method has been generated.
        ImmutableArray<Diagnostic> analyzer = output
            .WithAnalyzers([new CaparAnalyzer()])
            .GetAnalyzerDiagnosticsAsync()
            .GetAwaiter()
            .GetResult();

        return new GeneratorRun(
            [.. result.Diagnostics, .. analyzer],
            result.Results
                .Where(single => single.Generator.GetGeneratorType() == typeof(CaparGenerator))
                .SelectMany(single => single.GeneratedSources)
                .ToDictionary(source => source.HintName, source => source.SourceText.ToString(), StringComparer.Ordinal),
            [.. output.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)],
            output);
    }

    /// <summary>Compiles sources into an in-memory assembly another compilation can reference.</summary>
    /// <remarks>Used to put handlers in "another project" without a second project on disk.</remarks>
    public static MetadataReference Library(string assemblyName, params string[] sources) =>
        Library(assemblyName, [], withGenerator: false, sources);

    /// <summary>Like <see cref="Library(string, string[])"/>, optionally with the generator run over it first.</summary>
    public static MetadataReference Library(string assemblyName, IEnumerable<MetadataReference> extra, bool withGenerator, params string[] sources)
    {
        Compilation compilation = Compile(assemblyName, extra, sources);
        if (withGenerator)
        {
            CSharpGeneratorDriver
                .Create([new CaparGenerator().AsSourceGenerator()], parseOptions: ParseOptions)
                .RunGeneratorsAndUpdateCompilation(compilation, out compilation, out _);
        }

        using MemoryStream image = new();
        Microsoft.CodeAnalysis.Emit.EmitResult emitted = compilation.Emit(image);
        emitted.Success.ShouldBeTrue(string.Join(Environment.NewLine, emitted.Diagnostics));
        return MetadataReference.CreateFromImage(image.ToArray());
    }

    /// <summary>
    /// Runs the generator twice with a change in between and reports how each tracked step's output was
    /// produced the second time.
    /// </summary>
    /// <remarks>
    /// The only way to see incrementality. A generator whose models capture a symbol still emits the right
    /// code, so nothing else fails; it just re-runs everything on every keystroke.
    /// </remarks>
    public static IReadOnlyDictionary<string, IncrementalStepRunReason[]> RunTwice(string[] original, string[] edited)
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new CaparGenerator().AsSourceGenerator()],
            parseOptions: ParseOptions,
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

        // The second compilation reuses the first one's references and changes only syntax trees, which is
        // what an edit in an IDE does.
        CSharpCompilation first = Compile("Subject", [], original);
        driver = driver.RunGenerators(first);

        CSharpCompilation second = first.RemoveAllSyntaxTrees().AddSyntaxTrees(edited.Select(Parse));
        driver = driver.RunGenerators(second);

        return driver.GetRunResult().Results[0].TrackedSteps.ToDictionary(
            step => step.Key,
            step => step.Value.SelectMany(run => run.Outputs).Select(output => output.Reason).ToArray(),
            StringComparer.Ordinal);
    }

    private static CSharpCompilation Compile(string assemblyName, IEnumerable<MetadataReference> extra, string[] sources, bool strict = false) =>
        CSharpCompilation.Create(
            assemblyName,
            sources.Select((source, index) => Parse(source, index, strict)),
            References.Concat(extra),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable,
                generalDiagnosticOption: strict ? ReportDiagnostic.Error : ReportDiagnostic.Default,
                // Test sources carry no documentation of their own; the point of the strict run is the
                // generated code's comments, which are on internal members CS1591 never asks about anyway.
                specificDiagnosticOptions: strict ? [new("CS1591", ReportDiagnostic.Suppress)] : null));

    private static SyntaxTree Parse(string source, int index) => Parse(source, index, strict: false);

    private static SyntaxTree Parse(string source, int index, bool strict) =>
        CSharpSyntaxTree.ParseText(
            source,
            strict ? ParseOptions.WithDocumentationMode(DocumentationMode.Diagnose) : ParseOptions,
            path: $"File{index}.cs");

    private static ImmutableArray<MetadataReference> BuildReferences()
    {
        // Everything the test host was started with, rather than what happens to be loaded. Loading is lazy,
        // so an untouched assembly is simply absent, and the first to go missing is whichever forwards a type
        // the generated code names. That looks exactly like a generator bug and is not one.
        ImmutableArray<MetadataReference>.Builder builder = ImmutableArray.CreateBuilder<MetadataReference>();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string trusted)
        {
            foreach (string path in trusted.Split(Path.PathSeparator))
            {
                if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && seen.Add(path))
                {
                    builder.Add(MetadataReference.CreateFromFile(path));
                }
            }
        }

        foreach (Type anchor in (Type[])[typeof(IRequest<>), typeof(CaparOptions), typeof(Microsoft.Extensions.DependencyInjection.IServiceCollection)])
        {
            if (seen.Add(anchor.Assembly.Location))
            {
                builder.Add(MetadataReference.CreateFromFile(anchor.Assembly.Location));
            }
        }

        return builder.ToImmutable();
    }
}
