using Microsoft.Extensions.DependencyInjection;

namespace Scenarios;

public static partial class Scenarios
{
    public static Task<string> HandlerTakesAnotherHandlerThroughItsInterface() =>
        Run.Scenario(async services => await services.GetRequiredService<ISender>().Send(new Summary("tools")));

    public static Task<string> HandlerTakesAnotherHandlerThroughItsInterfaceWhenScoped() =>
        Run.Scenario(async services => await services.GetRequiredService<ISender>().Send(new Summary("nails")), lifetime: ServiceLifetime.Scoped);

    public static Task<string> RequestHandlerResolvedThroughItsInterface() =>
        Run.Scenario(async services => await services.GetRequiredService<IRequestHandler<Tally, int>>().Handle(new Tally("glue"), default));

    public static Task<string> NotificationHandlersResolvedThroughTheirInterface() =>
        Run.Scenario(async services =>
        {
            foreach (INotificationHandler<Doorbell> handler in services.GetServices<INotificationHandler<Doorbell>>())
            {
                await handler.Handle(new Doorbell("low"), default);
            }

            return null;
        });

    public static Task<string> PublishStillReachesEachNotificationHandlerOnce() =>
        Run.Scenario(async services =>
        {
            await services.GetRequiredService<IPublisher>().Publish(new Doorbell("high"));
            return null;
        });
}
