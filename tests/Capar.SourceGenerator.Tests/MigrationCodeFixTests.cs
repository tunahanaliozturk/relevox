using System.Collections.Immutable;
using Capar.CodeFixes;
using Capar.Migration;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Capar.SourceGenerator.Tests;

/// <summary>
/// The code fix that moves MediatR code to Capar, run against code written for MediatR 12 with the MediatR
/// package already gone, as it is after step 1 of the migration guide.
/// </summary>
/// <remarks>
/// Every test runs one "Fix all in solution" started on CPR0007, as the IDE does, and the result then has to
/// build with the generator, so a fix that leaves code which does not compile, or needs a second run, cannot
/// pass. The cases come from a review that tried to break it.
/// </remarks>
public sealed class MigrationCodeFixTests
{
    private const string Handlers = """
        using MediatR;
        using MediatR.Pipeline;
        using Microsoft.Extensions.DependencyInjection;
        using System.Threading;
        using System.Threading.Tasks;

        namespace Shop;

        public sealed record Ping(int Value) : IRequest<int>;

        public sealed class PingHandler : IRequestHandler<Ping, int>
        {
            public Task<int> Handle(Ping request, CancellationToken cancellationToken) => Task.FromResult(request.Value);
        }

        public sealed record Ring : MediatR.IRequest;

        public sealed class RingHandler : MediatR.IRequestHandler<Ring>
        {
            public Task Handle(Ring request, CancellationToken cancellationToken) => Task.CompletedTask;
        }

        public sealed class Stamp<TRequest> : IRequestPreProcessor<TRequest> where TRequest : notnull
        {
            public Task Process(TRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
        }

        public sealed class Logging<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
        {
            public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken) => next(cancellationToken);
        }
        """;

    [Fact]
    public async Task Usings_qualified_names_and_the_registration_are_moved_and_the_result_builds()
    {
        string startup = """
            using MediatR;
            using Microsoft.Extensions.DependencyInjection;

            namespace Shop;

            public static class Startup
            {
                public static IServiceCollection Block(IServiceCollection services) =>
                    services.AddMediatR(options =>
                    {
                        options.RegisterServicesFromAssemblyContaining<Ping>();
                        options.LicenseKey = "key";
                        options.AddOpenBehavior(typeof(Logging<,>));
                        options.AddOpenRequestPreProcessor(typeof(Stamp<>));
                    });

                public static IServiceCollection Chained(IServiceCollection services) =>
                    services.AddMediatR(options => options.RegisterServicesFromAssembly(typeof(Startup).Assembly).AddOpenBehavior(typeof(Logging<,>)));

                public static IServiceCollection ScanOnly(IServiceCollection services) =>
                    services.AddMediatR(options => options.RegisterServicesFromAssembly(typeof(Startup).Assembly));

                public static IServiceCollection OldStyle(IServiceCollection services) =>
                    services.AddMediatR(typeof(Startup));

                public static IServiceCollection Typed(IServiceCollection services) =>
                    services.AddMediatR((MediatRServiceConfiguration options) =>
                    {
                        options.RegisterGenericHandlers = true;
                        options.Lifetime = ServiceLifetime.Scoped;
                    });
            }
            """;

        string[] fixedSources = await Migrate(Handlers, startup);

        fixedSources[0].ShouldContain("using Capar;");
        fixedSources[0].ShouldContain("using Capar.Pipeline;");
        fixedSources[0].ShouldNotContain("MediatR");

        fixedSources[1].ShouldNotContain("MediatR");
        fixedSources[1].ShouldNotContain("RegisterServicesFrom");
        fixedSources[1].ShouldNotContain("LicenseKey");
        fixedSources[1].ShouldNotContain("RegisterGenericHandlers");
        fixedSources[1].ShouldContain("options.AddOpenBehavior(typeof(Logging<,>));");
        fixedSources[1].ShouldContain("options => options.AddOpenBehavior(typeof(Logging<,>))");
        fixedSources[1].ShouldContain("services.AddCapar()");
        fixedSources[1].ShouldContain("(CaparOptions options) =>");
        fixedSources[1].ShouldContain("options.Lifetime = ServiceLifetime.Scoped;");

        ShouldBuildWithTheGenerator(fixedSources);
    }

    [Fact]
    public async Task A_MediatR_namespace_with_no_Capar_counterpart_is_left_for_the_compiler_to_point_at()
    {
        string[] fixedSources = await Migrate("""
            using MediatR;
            using MediatR.Registration;

            public sealed record Ping : IRequest<int>;
            """);

        fixedSources[0].ShouldContain("using Capar;");
        fixedSources[0].ShouldContain("using MediatR.Registration;");
    }

    [Fact]
    public async Task Nothing_is_offered_for_an_unrelated_missing_type()
    {
        string[] fixedSources = await Migrate("public sealed class Holder { public Missing? Value { get; set; } }");

        fixedSources[0].ShouldContain("public Missing? Value");
    }

    [Fact]
    public async Task The_migration_sample_before_becomes_the_sample_after()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Capar.slnx")))
        {
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Capar.slnx not found above the test output.");
        }

        string Read(string side, string file) => File.ReadAllText(Path.Combine(root, "samples", "Migration", side, file));
        static string Code(string source) => string.Concat(source.ReplaceLineEndings("\n").Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("//", StringComparison.Ordinal)));

        string[] migrated = await Migrate(Read("Before", "Orders/Orders.cs"), Read("Before", "App/Program.cs"));

        Code(migrated[0]).ShouldBe(Code(Read("After", "Orders/Orders.cs")));
        Code(migrated[1]).ShouldBe(Code(Read("After", "App/Program.cs")));
        ShouldBuildWithTheGenerator(migrated, application: true);
    }

    [Fact]
    public async Task Comments_and_directives_around_removed_settings_survive()
    {
        string[] fixedSources = await Migrate(Handlers, """
            using MediatR;
            using Microsoft.Extensions.DependencyInjection;

            namespace Shop;

            public static class Startup
            {
                public static IServiceCollection Configure(IServiceCollection services) =>
                    services.AddMediatR(options =>
                    {
            #if DEBUG
                        options.AddOpenBehavior(typeof(Logging<,>));
            #endif
                        options.RegisterServicesFromAssembly(typeof(Startup).Assembly);
                        // Lifetimes match the web host's.
                        options.LicenseKey = "key";
                        options.Lifetime = ServiceLifetime.Scoped;
                    });
            }
            """);

        fixedSources[1].ShouldContain("#if DEBUG");
        fixedSources[1].ShouldContain("#endif");
        fixedSources[1].ShouldContain("options.AddOpenBehavior(typeof(Logging<,>));");
        fixedSources[1].ShouldContain("// Lifetimes match the web host's.");
        fixedSources[1].ShouldNotContain("LicenseKey");

        // Every line left in the lambda keeps its own indentation: a removed statement's does not pile onto it.
        string[] indents = [.. fixedSources[1].ReplaceLineEndings("\n").Split('\n').Where(line => line.TrimStart().StartsWith("options.", StringComparison.Ordinal) || line.TrimStart().StartsWith("// Lifetimes", StringComparison.Ordinal))
            .Select(line => line[..(line.Length - line.TrimStart().Length)]).Distinct()];
        indents.ShouldHaveSingleItem();
        ShouldBuildWithTheGenerator(fixedSources);
    }

    [Fact]
    public async Task Removed_settings_nested_in_statements_and_lambdas_leave_code_that_builds()
    {
        string[] fixedSources = await Migrate(Handlers, """
            using MediatR;
            using Microsoft.Extensions.DependencyInjection;
            using System.Collections.Generic;
            using System.Linq;
            using System.Reflection;

            namespace Shop;

            public static class Startup
            {
                public static IServiceCollection Configure(IServiceCollection services, bool scan, List<Assembly> plugins) =>
                    services.AddMediatR(options =>
                    {
                        if (scan) options.RegisterServicesFromAssembly(typeof(Startup).Assembly);
                        foreach (Assembly plugin in plugins) { options.RegisterServicesFromAssembly(plugin); }
                        plugins.ToList().ForEach(plugin => options.RegisterServicesFromAssembly(plugin));
                        if (scan) { options.LicenseKey = "key"; }
                        options.AddOpenBehavior(typeof(Logging<,>));
                    });
            }
            """);

        fixedSources[1].ShouldNotContain("RegisterServicesFrom");
        fixedSources[1].ShouldContain("options.AddOpenBehavior(typeof(Logging<,>));");
        ShouldBuildWithTheGenerator(fixedSources);
    }

    [Fact]
    public async Task Configuration_passed_other_ways_is_kept_or_left_for_a_person()
    {
        string[] fixedSources = await Migrate(Handlers, """
            using MediatR;
            using Microsoft.Extensions.DependencyInjection;
            using System;

            namespace Shop;

            public static class Startup
            {
                public static IServiceCollection MethodGroup(IServiceCollection services) => services.AddMediatR(Configure);

                public static IServiceCollection Anonymous(IServiceCollection services) =>
                    services.AddMediatR(delegate (MediatRServiceConfiguration options) { options.RegisterServicesFromAssembly(typeof(Startup).Assembly); options.AddOpenBehavior(typeof(Logging<,>)); });

                public static IServiceCollection Eleven(IServiceCollection services) =>
                    services.AddMediatR(options => options.AddOpenBehavior(typeof(Logging<,>)), typeof(Startup).Assembly);

                public static IServiceCollection Named(IServiceCollection services) =>
                    services.AddMediatR(configuration: options => options.AddOpenBehavior(typeof(Logging<,>)));

                public static IServiceCollection Static(IServiceCollection services) =>
                    MediatRServiceCollectionExtensions.AddMediatR(services, options => options.AddOpenBehavior(typeof(Logging<,>)));

                public static IServiceCollection Object(IServiceCollection services)
                {
                    MediatRServiceConfiguration options = new();
                    options.AddOpenBehavior(typeof(Logging<,>));
                    return services.AddMediatR(options);
                }

                private static void Configure(MediatRServiceConfiguration options) => options.AddOpenBehavior(typeof(Logging<,>));
            }
            """);

        string startup = fixedSources[1];
        startup.ShouldContain("services.AddCapar(Configure)");
        startup.ShouldContain("delegate (CaparOptions options) { options.AddOpenBehavior(typeof(Logging<,>)); }");
        startup.ShouldContain("services.AddCapar(options => options.AddOpenBehavior(typeof(Logging<,>)))");
        startup.ShouldNotContain("configuration:");
        startup.ShouldContain("CaparServiceCollectionExtensions.AddCapar(services, options => options.AddOpenBehavior(typeof(Logging<,>)))");

        // An options object built before the call cannot become a delegate without guessing; the error stays.
        startup.ShouldContain("return services.AddMediatR(options);");
    }

    [Fact]
    public async Task Every_form_of_a_MediatR_name_is_moved_by_one_fix_all()
    {
        string[] fixedSources = await Migrate(Handlers, """
            using Req = MediatR.IRequest<int>;
            using static MediatR.Unit;

            namespace Shop;

            public sealed record Pong : global::MediatR.IRequest<int>;

            public static class Uses
            {
                public static System.Threading.Tasks.Task<MediatR.Unit> Done() => System.Threading.Tasks.Task.FromResult(MediatR.Unit.Value);

                public static MediatR.Unit Imported() => Value;

                public static string Name() => nameof(MediatR.IMediator);

                public static System.Type Spaced() => typeof(MediatR . IRequest<int>);
            }
            """);

        string uses = fixedSources[1];
        uses.ShouldNotContain("MediatR");
        uses.ShouldContain("using Req = Capar.IRequest<int>;");
        uses.ShouldContain("using static Capar.Unit;");
        uses.ShouldContain("global::Capar.IRequest<int>");
        uses.ShouldContain("Task.FromResult(Capar.Unit.Value)");
        uses.ShouldContain("Req");
        ShouldBuildWithTheGenerator(fixedSources);
    }

    [Fact]
    public async Task A_namespace_merely_called_MediatR_and_unmapped_MediatR_names_are_left_alone()
    {
        string[] fixedSources = await Migrate("""
            namespace Company.Shared { public sealed class Marker; }

            public static class Uses
            {
                public static object? A() => null as Company.MediatR.Foo;

                public static object? B() => null as MediatR.Registration.ServiceRegistrar;
            }
            """);

        fixedSources[0].ShouldContain("Company.MediatR.Foo");
        fixedSources[0].ShouldContain("MediatR.Registration.ServiceRegistrar");
    }

    /// <summary>
    /// Runs the fix the way the IDE's "Fix all in solution" does: once, started on CPR0007, over every CPR0007
    /// the analyzer reports, merged by the batch fixer.
    /// </summary>
    private static async Task<string[]> Migrate(params string[] sources)
    {
        using AdhocWorkspace workspace = new();
        Project project = workspace.AddProject("Shop", LanguageNames.CSharp)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable))
            .WithParseOptions(new CSharpParseOptions(LanguageVersion.Preview).WithPreprocessorSymbols("DEBUG"))
            .WithMetadataReferences(GeneratorHarness.References);

        List<DocumentId> documents = [];
        for (int index = 0; index < sources.Length; index++)
        {
            Document document = project.AddDocument($"File{index}.cs", sources[index]);
            documents.Add(document.Id);
            project = document.Project;
        }

        MigrateFromMediatR fix = new();
        AnalyzerDiagnostics provider = new();
        ImmutableArray<Diagnostic> all = await AnalyzerDiagnostics.Collect(project, CancellationToken.None);
        if (!all.IsEmpty)
        {
            Document first = project.GetDocument(all[0].Location.SourceTree)!;
            FixAllContext context = new(first, fix, FixAllScope.Solution, "Move from MediatR to Capar", ["CPR0007"], provider, CancellationToken.None);
            CodeAction action = (await fix.GetFixAllProvider().GetFixAsync(context))!;
            ImmutableArray<CodeActionOperation> operations = await action.GetOperationsAsync(CancellationToken.None);
            project = operations.OfType<ApplyChangesOperation>().Single().ChangedSolution.GetProject(project.Id)!;
        }

        return [.. await Task.WhenAll(documents.Select(async id => (await project.GetDocument(id)!.GetTextAsync()).ToString()))];
    }

    /// <summary>What the IDE would hand a fix-all: CPR0007 from the analyzer, per document or for the project.</summary>
    private sealed class AnalyzerDiagnostics : FixAllContext.DiagnosticProvider
    {
        public override async Task<IEnumerable<Diagnostic>> GetAllDiagnosticsAsync(Project project, CancellationToken cancellationToken) =>
            await Collect(project, cancellationToken).ConfigureAwait(false);

        public override async Task<IEnumerable<Diagnostic>> GetDocumentDiagnosticsAsync(Document document, CancellationToken cancellationToken) =>
            (await Collect(document.Project, cancellationToken).ConfigureAwait(false)).Where(diagnostic => diagnostic.Location.SourceTree == document.GetSyntaxTreeAsync(cancellationToken).Result);

        public override Task<IEnumerable<Diagnostic>> GetProjectDiagnosticsAsync(Project project, CancellationToken cancellationToken) =>
            Task.FromResult(Enumerable.Empty<Diagnostic>());

        public static async Task<ImmutableArray<Diagnostic>> Collect(Project project, CancellationToken cancellationToken)
        {
            Compilation compilation = (await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false))!;
            return await compilation.WithAnalyzers([new MediatRUsageAnalyzer()]).GetAnalyzerDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <param name="sources">The migrated files.</param>
    /// <param name="application">
    /// Build as an SDK console app does: an executable, with the implicit usings the sample's project files get.
    /// </param>
    private static void ShouldBuildWithTheGenerator(string[] sources, bool application = false)
    {
        const string ImplicitUsings = "global using System; global using System.Collections.Generic; global using System.IO; global using System.Linq; global using System.Net.Http; global using System.Threading; global using System.Threading.Tasks;";
        string[] files = application ? [.. sources, ImplicitUsings] : sources;

        CSharpCompilation compilation = CSharpCompilation.Create(
            "Shop",
            files.Select((source, index) => CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview), $"File{index}.cs")),
            GeneratorHarness.References,
            new CSharpCompilationOptions(application ? OutputKind.ConsoleApplication : OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        CSharpGeneratorDriver
            .Create([new CaparGenerator().AsSourceGenerator()], parseOptions: new CSharpParseOptions(LanguageVersion.Preview))
            .RunGeneratorsAndUpdateCompilation(compilation, out Compilation output, out _);

        output.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
    }
}
