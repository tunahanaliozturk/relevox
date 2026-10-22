using Microsoft.CodeAnalysis;

namespace Relevox.SourceGenerator.Tests;

/// <summary>The message map's way out of the build (ADR 0014): a constant in the compilation, only when asked for.</summary>
public sealed class MessageMapTests
{
    private const string Handlers = """
        using Microsoft.Extensions.DependencyInjection; using Relevox; using System.Threading; using System.Threading.Tasks;
        namespace Shop;
        public sealed record Ping(string Text) : IRequest<string>;
        public sealed class PingHandler : IRequestHandler<Ping, string>
        {
            public Task<string> Handle(Ping request, CancellationToken cancellationToken) => Task.FromResult(request.Text);
        }
        public sealed record Touch : IRequest;
        public sealed class TouchHandler : IRequestHandler<Touch>
        {
            public Task Handle(Touch request, CancellationToken cancellationToken) => Task.CompletedTask;
        }
        """;

    [Fact]
    public void With_the_switch_on_the_map_is_compiled_into_the_assembly()
    {
        GeneratorRun run = GeneratorHarness.RunWithMessageMap(Handlers);

        run.CompilationErrors.ShouldBeEmpty();
        Json(run.Compilation).ShouldBe("""
            {
              "schemaVersion": 1,
              "requests": [
                "Shop.Ping",
                "Shop.Touch"
              ]
            }

            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void An_abstract_request_is_left_out_as_the_mediator_leaves_it_out()
    {
        GeneratorRun run = GeneratorHarness.RunWithMessageMap(Handlers, """
            using Relevox; using System.Threading; using System.Threading.Tasks;
            namespace Shop;
            public abstract record Query : IRequest<int>;
            public sealed class QueryHandler : IRequestHandler<Query, int>
            {
                public Task<int> Handle(Query request, CancellationToken cancellationToken) => Task.FromResult(1);
            }
            """);

        run.CompilationErrors.ShouldBeEmpty();
        Json(run.Compilation).ShouldNotBeNull().ShouldNotContain("Shop.Query");
    }

    [Fact]
    public void With_the_switch_off_the_generated_code_is_what_it_was()
    {
        GeneratorRun off = GeneratorHarness.RunStrict(Handlers);
        GeneratorRun on = GeneratorHarness.RunWithMessageMap(Handlers);

        off.Sources.Keys.ShouldNotContain("RelevoxMessageMap.g.cs");
        on.Sources.Where(source => source.Key != "RelevoxMessageMap.g.cs").ShouldBe(off.Sources);
    }

    [Fact]
    public void A_project_using_another_projects_mediator_has_no_map()
    {
        MetadataReference application = GeneratorHarness.Library("Shop.Application", [], withGenerator: true, Handlers + """

            public static class Setup
            {
                public static IServiceCollection AddApplication(IServiceCollection services) => services.AddRelevox();
            }
            """);

        GeneratorRun run = GeneratorHarness.RunWithMessageMap("Shop.Api", [application], """
            public static class Program
            {
                public static void Configure(Microsoft.Extensions.DependencyInjection.IServiceCollection services) => Shop.Setup.AddApplication(services);
            }
            """);

        run.Sources.ShouldBeEmpty();
    }

    [Fact]
    public void An_edit_that_changes_no_message_leaves_the_map_alone()
    {
        var steps = GeneratorHarness.RunTwice(
            [Handlers, "public static class Util { public static int One() => 1; }"],
            [Handlers, "public static class Util { public static int One() => 2; }"],
            messageMap: true);

        steps[TrackingNames.MessageMap].ShouldAllBe(reason => reason == IncrementalStepRunReason.Cached || reason == IncrementalStepRunReason.Unchanged);
    }

    // What Relevox.Build reads: the constant's value, not the generated source text.
    private static string? Json(Compilation compilation) =>
        compilation.GetTypeByMetadataName("Subject.RelevoxMessageMap")?.GetMembers("Json").OfType<IFieldSymbol>().Single().ConstantValue as string;
}
