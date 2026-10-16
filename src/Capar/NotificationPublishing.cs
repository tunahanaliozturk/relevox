namespace Capar;

/// <summary>Decides how a notification's handlers are run: one after another, all at once, or otherwise.</summary>
public interface INotificationPublisher
{
    /// <summary>Runs the handlers.</summary>
    /// <param name="handlerExecutors">One per handler, in registration order.</param>
    /// <param name="notification">The notification.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes when the publisher considers the notification published.</returns>
    Task Publish(IEnumerable<NotificationHandlerExecutor> handlerExecutors, INotification notification, CancellationToken cancellationToken);
}

/// <summary>A notification handler and the call that runs it.</summary>
/// <param name="HandlerInstance">The handler.</param>
/// <param name="HandlerCallback">Calls the handler with the notification.</param>
public sealed record NotificationHandlerExecutor(object HandlerInstance, Func<INotification, CancellationToken, Task> HandlerCallback);
