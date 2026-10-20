using Microsoft.Extensions.DependencyInjection;

namespace Scenarios;

public static partial class Scenarios
{
    public static Task<string> ConstrainedGenericExceptionHandlerAndActionRun() =>
        Run.Scenario(services => Run.Timing(() => services.GetRequiredService<ISender>().Send(new Restock("bolts"))));

    public static Task<string> ConstrainedGenericExceptionHandlerStaysOutOfTheWay() =>
        Run.Scenario(services => Run.Timing(() => services.GetRequiredService<ISender>().Send(new Restock("plenty"))));
}
