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

    public static Task<string> AbstractNotificationHandlersResolvedThroughTheirInterface() =>
        Run.Scenario(async services =>
        {
            foreach (INotificationHandler<Bulletin> handler in services.GetServices<INotificationHandler<Bulletin>>())
            {
                await handler.Handle(new PostedBulletin("closed early"), default);
            }

            return null;
        });

    public static Task<string> InterfaceNotificationHandlersResolvedThroughTheirInterface() =>
        Run.Scenario(async services =>
        {
            foreach (INotificationHandler<IMemo> handler in services.GetServices<INotificationHandler<IMemo>>())
            {
                await handler.Handle(new Memo("back at noon"), default);
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
