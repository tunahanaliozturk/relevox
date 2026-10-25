using Microsoft.Extensions.DependencyInjection;

namespace Scenarios;

public static partial class Scenarios
{
    public static Task<string> PipelineRunsInRegistrationOrder() =>
        Pipeline(async services => await services.GetRequiredService<ISender>().Send(new Ping("p")));

    public static Task<string> PipelineCanShortCircuit() =>
        Pipeline(async services => await services.GetRequiredService<ISender>().Send(new Cached("k")));

    // AuditedBehavior is constrained on the request and OutcomeBehavior on the response: each runs only where its
    // constraint holds, as the container closes it.
    public static Task<string> PipelineSkipsBehavioursWhoseConstraintsDoNotHold() =>
        Pipeline(async services =>
        {
            ISender sender = services.GetRequiredService<ISender>();
            return $"{await sender.Send(new Judge())} {await sender.Send(new Cached("c"))}";
        });

    public static Task<string> PipelineCanCallNextTwice() =>
        Pipeline(async services => await services.GetRequiredService<ISender>().Send(new Flaky()));

    public static Task<string> PipelineCanReplaceTheToken() =>
        Pipeline(async services => await services.GetRequiredService<ISender>().Send(new Retoken()));

    public static Task<string> PipelineForRequestWithoutResponse() =>
        Pipeline(async services =>
        {
            await services.GetRequiredService<ISender>().Send(new Ring(1));
            return "done";
        });

    public static Task<string> PipelineThroughSendObject() =>
        Pipeline(async services => await services.GetRequiredService<ISender>().Send((object)new Ping("o")));

    public static Task<string> PipelineThroughSendObjectWithoutResponse() =>
        Pipeline(async services => await services.GetRequiredService<ISender>().Send((object)new Ring(2)));

    public static Task<string> PipelineWhenTheHandlerThrows() =>
        Pipeline(services => Run.Timing(() => services.GetRequiredService<ISender>().Send(new Explode())));

    public static Task<string> PipelineWithScopedMediator() =>
        Run.Scenario(
            async services => await services.GetRequiredService<ISender>().Send(new Ping("scoped")),
            lifetime: ServiceLifetime.Scoped,
            profile: Profile.Pipeline);

    public static Task<string> StreamPipelineRunsInRegistrationOrder() =>
        Pipeline(services => Run.StreamTiming(() => services.GetRequiredService<ISender>().CreateStream(new Countdown(2))));

    public static Task<string> StreamPipelineCanShortCircuit() =>
        Pipeline(services => Run.StreamTiming(() => services.GetRequiredService<ISender>().CreateStream(new Ticker(3))));

    public static Task<string> StreamPipelineThroughCreateStreamObject() =>
        Pipeline(services => Run.StreamTiming(() => services.GetRequiredService<ISender>().CreateStream((object)new Countdown(1))));

    private static Task<string> Pipeline(Func<IServiceProvider, Task<object?>> body) =>
        Run.Scenario(body, profile: Profile.Pipeline);

    // A behaviour registered before the library's call is outside its built-in behaviours, in both.
    public static Task<string> EarlyBehaviourAroundABuiltInWithWork() =>
        Run.Scenario(async services => await services.GetRequiredService<ISender>().Send(new Ping("early")), profile: Profile.EarlyBehavior);

    public static Task<string> EarlyBehaviourNextToAnIdleBuiltIn() =>
        Run.Scenario(
            async services =>
            {
                await services.GetRequiredService<ISender>().Send(new Ring(2));
                return "done";
            },
            profile: Profile.EarlyBehavior);

    public static Task<string> AnIdleBuiltInStillMakesTheSendAsynchronous() =>
        Run.Scenario(services => Run.Timing(() => services.GetRequiredService<ISender>().Send(new Explode())), profile: Profile.EarlyBehavior);

    public static Task<string> AnIdleBuiltInSkippedTwiceRunsTheSameWay() =>
        Run.Scenario(
            async services =>
            {
                ISender sender = services.GetRequiredService<ISender>();
                await sender.Send(new Ring(1));
                await sender.Send(new Ring(1));
                return await sender.Send(new Ping("again"));
            },
            profile: Profile.EarlyBehavior);

    // MediatR's idle built-in is async and sits inside a behaviour registered first, so that behaviour sees a
    // faulted task, never a throw, and cannot catch what the handler throws.
    public static Task<string> ASyncBehaviourOutsideAnIdleBuiltInSeesAFaultedTask() =>
        Run.Scenario(services => Run.Timing(() => services.GetRequiredService<ISender>().Send(new Tripwire())), profile: Profile.EarlySyncBehavior);

    // Nothing async runs outside that behaviour, so its own throw leaves Send synchronously.
    public static Task<string> ASyncBehaviourOutsideAnIdleBuiltInThrowsSynchronously() =>
        Run.Scenario(services => Run.Timing(() => services.GetRequiredService<ISender>().Send(new Gatekept())), profile: Profile.EarlySyncBehavior);

    public static Task<string> AnActiveBuiltInInsideSyncBehavioursStillRuns() =>
        Run.Scenario(async services => await services.GetRequiredService<ISender>().Send(new Ping("inside")), profile: Profile.EarlySyncBehavior);
}
