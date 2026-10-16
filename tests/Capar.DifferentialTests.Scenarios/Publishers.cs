using Microsoft.Extensions.DependencyInjection;

namespace Scenarios;

/// <summary>
/// A publisher of its own, which runs handlers in reverse, to prove the executors are used as given. It records
/// every call, including calls with no handlers at all.
/// </summary>
public sealed class ReversePublisher(Trace trace) : INotificationPublisher
{
    public async Task Publish(IEnumerable<NotificationHandlerExecutor> handlerExecutors, INotification notification, CancellationToken cancellationToken)
    {
        NotificationHandlerExecutor[] executors = [.. handlerExecutors];
        trace.Add($"ReversePublisher({notification.GetType().Name}, {executors.Length})");
        foreach (NotificationHandlerExecutor executor in executors.Reverse())
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

    public static Task<string> PublishWithACustomPublisherAndNoHandlers() =>
        Run.Scenario(
            async services =>
            {
                await services.GetRequiredService<IPublisher>().Publish(new Quiet());
                return "done";
            },
            profile: Profile.ReversePublisher);

    public static Task<string> PublishWithAPublisherRegisteredAfterward() =>
        Run.Scenario(
            async services =>
            {
                await services.GetRequiredService<IPublisher>().Publish(new Alarm("east"));
                return "done";
            },
            profile: Profile.LatePublisher);

    public static Task<string> PublishObjectWithACustomPublisher() =>
        Run.Scenario(
            async services =>
            {
                await services.GetRequiredService<IPublisher>().Publish((object)new Alarm("north"));
                return "done";
            },
            profile: Profile.ReversePublisher);
}
