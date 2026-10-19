namespace Relevox;

/// <summary>Handles one request type and returns its response.</summary>
/// <typeparam name="TRequest">The request handled.</typeparam>
/// <typeparam name="TResponse">The response returned.</typeparam>
public interface IRequestHandler<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The response.</returns>
    Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
}

/// <summary>Handles one request type that returns nothing.</summary>
/// <typeparam name="TRequest">The request handled.</typeparam>
public interface IRequestHandler<in TRequest>
    where TRequest : IRequest
{
    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes when the request has been handled.</returns>
    Task Handle(TRequest request, CancellationToken cancellationToken);
}

/// <summary>One of possibly many handlers for a notification.</summary>
/// <typeparam name="TNotification">The notification handled.</typeparam>
public interface INotificationHandler<in TNotification>
    where TNotification : INotification
{
    /// <summary>Handles the notification.</summary>
    /// <param name="notification">The notification.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes when this handler is done.</returns>
    Task Handle(TNotification notification, CancellationToken cancellationToken);
}

/// <summary>Handles one stream request type.</summary>
/// <typeparam name="TRequest">The request handled.</typeparam>
/// <typeparam name="TResponse">The type of each item in the stream.</typeparam>
public interface IStreamRequestHandler<in TRequest, out TResponse>
    where TRequest : IStreamRequest<TResponse>
{
    /// <summary>Produces the stream.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <returns>The items, produced as they are enumerated.</returns>
    IAsyncEnumerable<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
}
