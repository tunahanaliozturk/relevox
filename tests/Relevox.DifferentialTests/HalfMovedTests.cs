extern alias onbridge;

namespace Relevox.DifferentialTests;

/// <summary>
/// Runs every shared scenario half moved through Relevox.MediatRBridge and requires what MediatR alone gives
/// (#131, ADR 0022).
/// </summary>
/// <remarks>
/// The requests in Messages.cs are on Relevox and everything else is on MediatR, as in the middle of a migration
/// done one feature at a time. A scenario listed in <see cref="Differences"/> is expected to differ, for the
/// reason given there and in docs/compatibility.md; any other difference fails.
/// </remarks>
public sealed class HalfMovedTests
{
    private static readonly Type OnBridge = typeof(onbridge::Scenarios.Scenarios);

    // MediatR's behaviours and processors are registered with MediatR only, and a moved request runs Relevox's
    // pipeline, so they do not run for it.
    private const string Pipeline = "MediatR's pipeline does not run for a moved request";

    // MediatR's exception handlers make every MediatR send asynchronous. Relevox has none here, so a moved request
    // whose handler throws, or that has no handler, throws out of Send instead of faulting the task.
    private const string Synchronous = "a moved request without Relevox exception handlers can throw synchronously";

    private static readonly Dictionary<string, string> Differences = new(StringComparer.Ordinal)
    {
        ["AnActiveBuiltInInsideSyncBehavioursStillRuns"] = Pipeline,
        ["AnIdleBuiltInSkippedTwiceRunsTheSameWay"] = Pipeline,
        ["AnIdleBuiltInStillMakesTheSendAsynchronous"] = Pipeline,
        ["EarlyBehaviourAroundABuiltInWithWork"] = Pipeline,
        ["EarlyBehaviourNextToAnIdleBuiltIn"] = Pipeline,
        ["PipelineForRequestWithoutResponse"] = Pipeline,
        ["PipelineRunsInRegistrationOrder"] = Pipeline,
        ["PipelineThroughSendObject"] = Pipeline,
        ["PipelineThroughSendObjectWithoutResponse"] = Pipeline,
        ["PipelineWhenTheHandlerThrows"] = Pipeline,
        ["PipelineWithScopedMediator"] = Pipeline,
        ["SendDerivedRequestWhoseBaseHasHandler"] = Synchronous,
        ["SendToHandlerThatThrows"] = Synchronous,
        ["SendWithoutHandler"] = Synchronous,
    };

    [Theory]
    [MemberData(nameof(DifferentialTests.ScenarioNames), MemberType = typeof(DifferentialTests))]
    public async Task A_half_moved_application_behaves_like_MediatR(string scenario)
    {
        string mediatr = await DifferentialTests.Invoke(DifferentialTests.OnMediatR, scenario);
        string bridged = await DifferentialTests.Invoke(OnBridge, scenario);

        if (Differences.TryGetValue(scenario, out string? reason))
        {
            bridged.ShouldNotBe(mediatr, $"Listed as different ({reason}), but now the same.");
        }
        else
        {
            bridged.ShouldBe(mediatr, $"MediatR: {mediatr}{Environment.NewLine}Bridged: {bridged}");
        }
    }
}
