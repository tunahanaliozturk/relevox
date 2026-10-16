using Microsoft.Extensions.DependencyInjection;

namespace Scenarios;

public static partial class Scenarios
{
    public static Task<string> PipelineRunsInRegistrationOrder() =>
        Pipeline(async services => await services.GetRequiredService<ISender>().Send(new Ping("p")));

    public static Task<string> PipelineCanShortCircuit() =>
        Pipeline(async services => await services.GetRequiredService<ISender>().Send(new Cached("k")));

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
}
