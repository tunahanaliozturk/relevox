using Microsoft.Extensions.DependencyInjection;

namespace Scenarios;

public static partial class Scenarios
{
    public static Task<string> ConstrainedGenericExceptionHandlerAndActionRun() =>
        Run.Scenario(services => Run.Timing(() => services.GetRequiredService<ISender>().Send(new Restock("nuts"))));

    // The thrown exception is a subclass of the constraint that no handler names.
    public static Task<string> ConstrainedGenericExceptionHandlerAndActionRunForASubclass() =>
        Run.Scenario(services => Run.Timing(() => services.GetRequiredService<ISender>().Send(new Restock("bolts"))));

    public static Task<string> ConstrainedGenericExceptionHandlerStaysOutOfTheWay() =>
        Run.Scenario(services => Run.Timing(() => services.GetRequiredService<ISender>().Send(new Restock("plenty"))));
}
