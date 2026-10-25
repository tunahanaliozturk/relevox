namespace Relevox;

/// <summary>The rest of the pipeline after a behaviour: the next behaviour, or the handler itself.</summary>
/// <typeparam name="TResponse">The response the pipeline produces.</typeparam>
/// <param name="cancellationToken">Cancels the remaining work.</param>
/// <returns>The response.</returns>
public delegate Task<TResponse> RequestHandlerDelegate<TResponse>(CancellationToken cancellationToken = default);

/// <summary>Wraps the handling of a request, for cross-cutting work such as validation or logging.</summary>
/// <typeparam name="TRequest">The request type wrapped.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
public interface IPipelineBehavior<in TRequest, TResponse>
    where TRequest : notnull
{
    /// <summary>Runs this behaviour and, unless it decides otherwise, the rest of the pipeline.</summary>
    /// <param name="request">The request.</param>
    /// <param name="next">The rest of the pipeline. Call it to continue; skip it to short-circuit.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The response.</returns>
    Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken);
}

/// <summary>The rest of a stream pipeline after a behaviour.</summary>
/// <typeparam name="TResponse">The type of each item.</typeparam>
/// <returns>The stream.</returns>
public delegate IAsyncEnumerable<TResponse> StreamHandlerDelegate<out TResponse>();

/// <summary>Wraps the handling of a stream request.</summary>
/// <typeparam name="TRequest">The stream request type wrapped.</typeparam>
/// <typeparam name="TResponse">The type of each item.</typeparam>
public interface IStreamPipelineBehavior<in TRequest, TResponse>
    where TRequest : notnull
{
    /// <summary>Runs this behaviour around the rest of the stream pipeline.</summary>
    /// <param name="request">The request.</param>
    /// <param name="next">The rest of the pipeline.</param>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <returns>The stream.</returns>
    IAsyncEnumerable<TResponse> Handle(TRequest request, StreamHandlerDelegate<TResponse> next, CancellationToken cancellationToken);
}

/// <summary>
/// Places a pipeline or stream behaviour by number instead of by registration order: a lower number runs further
/// out, seeing the request before the others do.
/// </summary>
/// <remarks>
/// A behaviour without the attribute counts as 0, and behaviours with the same number keep the order they were
/// registered in, so a pipeline with no attribute anywhere runs exactly as registered, as in MediatR. Built-in
/// processor and exception behaviours count as 0 too. The order is worked out once per request type, on its first
/// send (ADR 0019).
/// </remarks>
/// <param name="order">Where the behaviour runs: lower runs further out.</param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class PipelineOrderAttribute(int order) : Attribute
{
    /// <summary>Where the behaviour runs: lower runs further out.</summary>
    public int Order { get; } = order;
}
