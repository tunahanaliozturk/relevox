namespace Relevox;

/// <summary>Sends requests to their single handler.</summary>
public interface ISender
{
    /// <summary>Sends a request and returns its response.</summary>
    /// <typeparam name="TResponse">The response type.</typeparam>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The handler's response.</returns>
    Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default);

    /// <summary>Sends a request that returns nothing.</summary>
    /// <typeparam name="TRequest">The request type.</typeparam>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes when the request has been handled.</returns>
    Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
        where TRequest : IRequest;

    /// <summary>Sends a request whose type is only known at run time.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The handler's response, or null for a request that returns nothing.</returns>
    Task<object?> Send(object request, CancellationToken cancellationToken = default);

    /// <summary>Creates the stream for a stream request.</summary>
    /// <typeparam name="TResponse">The type of each item.</typeparam>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <returns>The stream.</returns>
    IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default);

    /// <summary>Creates the stream for a stream request whose type is only known at run time.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <returns>The stream, with each item boxed.</returns>
    IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default);
}

/// <summary>Publishes notifications to every handler registered for them.</summary>
public interface IPublisher
{
    /// <summary>Publishes a notification whose type is only known at run time.</summary>
    /// <param name="notification">The notification.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes when every handler is done.</returns>
    Task Publish(object notification, CancellationToken cancellationToken = default);

    /// <summary>Publishes a notification.</summary>
    /// <typeparam name="TNotification">The notification type.</typeparam>
    /// <param name="notification">The notification.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes when every handler is done.</returns>
    Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification;
}

/// <summary>Sends requests and publishes notifications.</summary>
public interface IMediator : ISender, IPublisher;
