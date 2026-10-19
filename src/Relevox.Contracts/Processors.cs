namespace Relevox.Pipeline;

/// <summary>Runs before a request's handler, after the behaviours registered ahead of it.</summary>
/// <typeparam name="TRequest">The request type, or a base type or interface of it.</typeparam>
public interface IRequestPreProcessor<in TRequest>
    where TRequest : notnull
{
    /// <summary>Processes the request before it is handled.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes when processing is done.</returns>
    Task Process(TRequest request, CancellationToken cancellationToken);
}

/// <summary>Runs after a request's handler has returned a response.</summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response type; <see cref="Unit"/> for a request that returns nothing.</typeparam>
public interface IRequestPostProcessor<in TRequest, in TResponse>
    where TRequest : notnull
{
    /// <summary>Processes the request and its response.</summary>
    /// <param name="request">The request.</param>
    /// <param name="response">The handler's response.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes when processing is done.</returns>
    Task Process(TRequest request, TResponse response, CancellationToken cancellationToken);
}
