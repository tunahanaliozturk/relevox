using System.Collections.Immutable;
using Capar.CodeFixes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;

namespace Capar.SourceGenerator.Tests;

/// <summary>
/// The code fix that moves MediatR code to Capar, run against code written for MediatR 12 with the MediatR
/// package already gone, as it is after step 1 of the migration guide.
/// </summary>
/// <remarks>
/// Fixes are applied one at a time, recompiling in between, until none is offered, which is what pressing the
/// light bulb repeatedly does and what "Fix all" does in one go. The result then has to build with the
/// generator, so a fix that leaves code which does not compile cannot pass.
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

    private static async Task<string[]> Migrate(params string[] sources)
    {
        using AdhocWorkspace workspace = new();
        Project project = workspace.AddProject("Shop", LanguageNames.CSharp)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable))
            .WithParseOptions(new CSharpParseOptions(LanguageVersion.Preview))
            .WithMetadataReferences(GeneratorHarness.References);

        List<DocumentId> documents = [];
        for (int index = 0; index < sources.Length; index++)
        {
            Document document = project.AddDocument($"File{index}.cs", sources[index]);
            documents.Add(document.Id);
            project = document.Project;
        }

        MigrateFromMediatR fix = new();
        for (int round = 0; round < 100; round++)
        {
            Compilation compilation = (await project.GetCompilationAsync())!;
            CodeAction? action = null;
            foreach (Diagnostic diagnostic in compilation.GetDiagnostics().Where(diagnostic => fix.FixableDiagnosticIds.Contains(diagnostic.Id)))
            {
                List<CodeAction> offered = [];
                CodeFixContext context = new(project.GetDocument(diagnostic.Location.SourceTree)!, diagnostic, (offer, _) => offered.Add(offer), CancellationToken.None);
                await fix.RegisterCodeFixesAsync(context);
                if (offered.Count > 0)
                {
                    action = offered[0];
                    break;
                }
            }

            if (action is null)
            {
                return [.. await Task.WhenAll(documents.Select(async id => (await project.GetDocument(id)!.GetTextAsync()).ToString()))];
            }

            ImmutableArray<CodeActionOperation> operations = await action.GetOperationsAsync(CancellationToken.None);
            project = operations.OfType<ApplyChangesOperation>().Single().ChangedSolution.GetProject(project.Id)!;
        }

        throw new InvalidOperationException("The code fix kept offering fixes; it is not converging.");
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
