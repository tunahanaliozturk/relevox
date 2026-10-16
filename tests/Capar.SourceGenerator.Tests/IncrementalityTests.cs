using Microsoft.CodeAnalysis;

namespace Capar.SourceGenerator.Tests;

/// <summary>
/// Asserts that the generator's cache is actually used.
/// </summary>
/// <remarks>
/// Nothing else would notice if it were not. A model that captures a symbol or a plain array still produces
/// the right code; it just re-runs the whole pipeline on every keystroke in every file of the solution.
/// </remarks>
public sealed class IncrementalityTests
{
    private const string Handler = """
        using Capar; using System.Threading; using System.Threading.Tasks;
        public sealed record Ping : IRequest<int>;
        public sealed class PingHandler : IRequestHandler<Ping, int>
        {
            public Task<int> Handle(Ping request, CancellationToken cancellationToken) => Task.FromResult(1);
        }
        """;

    [Fact]
    public void An_edit_unrelated_to_handlers_reuses_the_plan()
    {
        var steps = GeneratorHarness.RunTwice(
            [Handler, "public static class Util { public static int One() => 1; }"],
            [Handler, "public static class Util { public static int One() => 2; }"]);

        steps[TrackingNames.Plan].ShouldAllBe(reason => reason == IncrementalStepRunReason.Cached || reason == IncrementalStepRunReason.Unchanged);
        steps[TrackingNames.References].ShouldAllBe(reason => reason == IncrementalStepRunReason.Unchanged || reason == IncrementalStepRunReason.Cached);
    }

    [Fact]
    public void Adding_a_handler_changes_the_plan()
    {
        const string Another = """
            using Capar; using System.Threading; using System.Threading.Tasks;
            public sealed record Pong : IRequest<int>;
            public sealed class PongHandler : IRequestHandler<Pong, int>
            {
                public Task<int> Handle(Pong request, CancellationToken cancellationToken) => Task.FromResult(2);
            }
            """;

        var steps = GeneratorHarness.RunTwice([Handler], [Handler, Another]);

        steps[TrackingNames.Plan].ShouldContain(IncrementalStepRunReason.Modified);
    }
}
