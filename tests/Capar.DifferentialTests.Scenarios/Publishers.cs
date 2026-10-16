using Microsoft.Extensions.DependencyInjection;

namespace Scenarios;

/// <summary>A publisher of its own, which runs handlers in reverse, to prove the executors are used as given.</summary>
public sealed class ReversePublisher : INotificationPublisher
{
    public async Task Publish(IEnumerable<NotificationHandlerExecutor> handlerExecutors, INotification notification, CancellationToken cancellationToken)
    {
        foreach (NotificationHandlerExecutor executor in handlerExecutors.Reverse())
        {
            await executor.HandlerCallback(notification, cancellationToken);
        }
    }
}

public static partial class Scenarios
{
    public static Task<string> PublishWithTaskWhenAll() =>
        Run.Scenario(
            async services =>
            {
                await services.GetRequiredService<IPublisher>().Publish(new Alarm("west"));
                return "done";
            },
            profile: Profile.WhenAll);

    public static Task<string> PublishWithTaskWhenAllWhenAHandlerThrows() =>
        Run.Scenario(services => Run.Timing(() => services.GetRequiredService<IPublisher>().Publish(new Boom())), profile: Profile.WhenAll);

    public static Task<string> PublishWithACustomPublisher() =>
        Run.Scenario(
            async services =>
            {
                await services.GetRequiredService<IPublisher>().Publish(new Alarm("south"));
                return "done";
            },
            profile: Profile.ReversePublisher);

    public static Task<string> PublishObjectWithACustomPublisher() =>
        Run.Scenario(
            async services =>
            {
                await services.GetRequiredService<IPublisher>().Publish((object)new Alarm("north"));
                return "done";
            },
            profile: Profile.ReversePublisher);
}
