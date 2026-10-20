namespace Relevox.Testing;

/// <summary>
/// Remembers every notification the real mediator publishes, for integration tests that run the application's
/// own handlers and still want to check what was published.
/// </summary>
/// <remarks>
/// Registered by <c>AddRelevoxNotificationRecorder</c>, which puts a recording publisher in front of whichever
/// publisher was in force. Handlers run exactly as before.
/// </remarks>
public sealed class NotificationRecorder
{
    private readonly Lock _gate = new();
    private readonly List<object> _published = [];

    /// <summary>The notifications published so far that are a <typeparamref name="TNotification"/>, in order.</summary>
    /// <typeparam name="TNotification">The notification type, or <see cref="object"/> for all of them.</typeparam>
    /// <returns>A copy; later publishes do not change it.</returns>
    public IReadOnlyList<TNotification> Published<TNotification>()
    {
        lock (_gate)
        {
            return [.. _published.OfType<TNotification>()];
        }
    }

    /// <summary>Forgets what was published.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _published.Clear();
        }
    }

    internal void Record(INotification notification)
    {
        lock (_gate)
        {
            _published.Add(notification);
        }
    }
}

/// <summary>Records each notification, then hands it to the publisher that was in force.</summary>
internal sealed class RecordingPublisher(NotificationRecorder recorder, INotificationPublisher inner) : INotificationPublisher
{
    public Task Publish(IEnumerable<NotificationHandlerExecutor> handlerExecutors, INotification notification, CancellationToken cancellationToken)
    {
        recorder.Record(notification);
        return inner.Publish(handlerExecutors, notification, cancellationToken);
    }
}
