using Microsoft.Extensions.DependencyInjection;

namespace Scenarios;

public static partial class Scenarios
{
    public static Task<string> ExceptionFromTheHandlerSkipsPostProcessors() =>
        Run.Scenario(services => Run.Timing(() => services.GetRequiredService<ISender>().Send(new Guarded("handler"))), profile: Profile.Pipeline);

    public static Task<string> ExceptionFromAPreProcessorIsHandled() =>
        Run.Scenario(services => Run.Timing(() => services.GetRequiredService<ISender>().Send(new Guarded("pre"))), profile: Profile.Pipeline);

    public static Task<string> NoExceptionRunsPostProcessors() =>
        Run.Scenario(async services => await services.GetRequiredService<ISender>().Send(new Guarded("none")), profile: Profile.Pipeline);

    public static Task<string> SendStaysAsynchronousNextToASynchronousBehaviour() =>
        Run.Scenario(services => Run.Timing(() => services.GetRequiredService<ISender>().Send(new Brittle())), profile: Profile.SyncBehavior);

    public static Task<string> HandlingWithANullResponseRethrows() =>
        Run.Scenario(services => Run.Timing(() => services.GetRequiredService<ISender>().Send(new Nullish())));
}
