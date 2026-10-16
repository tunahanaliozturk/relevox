namespace Capar.NotificationPublishers;

/// <summary>
/// Runs handlers one after another, awaiting each, and stops at the first exception. The default.
/// </summary>
/// <remarks>
/// When this is the publisher in force, the generated mediator does not call it: it awaits the handlers
/// itself, in the same order with the same semantics, without building an executor per handler.
/// </remarks>
public sealed class ForeachAwaitPublisher : INotificationPublisher
{
    /// <inheritdoc />
    public async Task Publish(IEnumerable<NotificationHandlerExecutor> handlerExecutors, INotification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handlerExecutors);
        foreach (NotificationHandlerExecutor handler in handlerExecutors)
        {
            await handler.HandlerCallback(notification, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Starts every handler, then waits for all of them. Every handler runs even when one fails.</summary>
public sealed class TaskWhenAllPublisher : INotificationPublisher
{
    /// <inheritdoc />
    public Task Publish(IEnumerable<NotificationHandlerExecutor> handlerExecutors, INotification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handlerExecutors);
        Task[] tasks = [.. handlerExecutors.Select(handler => handler.HandlerCallback(notification, cancellationToken))];
        return Task.WhenAll(tasks);
    }
}
