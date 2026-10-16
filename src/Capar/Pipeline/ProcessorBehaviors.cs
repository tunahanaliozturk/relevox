namespace Capar.Pipeline;

/// <summary>
/// Implemented by built-in behaviours that do nothing when they have nothing to run, so the pipeline can
/// leave them out without changing what happens.
/// </summary>
/// <remarks>
/// Pre and post processor behaviours are registered for every request as soon as any processor exists, as
/// in MediatR, so their position in the pipeline matches. A request with no processors of its own would
/// still pay for an empty behaviour on every send; dropping it keeps such a request on the direct path.
/// </remarks>
internal interface IOptionalBehavior
{
    bool IsEmpty { get; }
}

/// <summary>Runs every <see cref="IRequestPreProcessor{TRequest}"/> for the request, in order, then the rest of the pipeline.</summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
public sealed class RequestPreProcessorBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>, IOptionalBehavior
    where TRequest : notnull
{
    private readonly IRequestPreProcessor<TRequest>[] _processors;

    /// <summary>Creates the behaviour over the registered pre-processors.</summary>
    /// <param name="preProcessors">The pre-processors, in registration order.</param>
    public RequestPreProcessorBehavior(IEnumerable<IRequestPreProcessor<TRequest>> preProcessors)
    {
        ArgumentNullException.ThrowIfNull(preProcessors);
        _processors = preProcessors as IRequestPreProcessor<TRequest>[] ?? [.. preProcessors];
    }

    bool IOptionalBehavior.IsEmpty => _processors.Length == 0;

    /// <inheritdoc />
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);
        foreach (IRequestPreProcessor<TRequest> processor in _processors)
        {
            await processor.Process(request, cancellationToken).ConfigureAwait(false);
        }

        return await next(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Runs the rest of the pipeline, then every <see cref="IRequestPostProcessor{TRequest, TResponse}"/>, in order.</summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
public sealed class RequestPostProcessorBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>, IOptionalBehavior
    where TRequest : notnull
{
    private readonly IRequestPostProcessor<TRequest, TResponse>[] _processors;

    /// <summary>Creates the behaviour over the registered post-processors.</summary>
    /// <param name="postProcessors">The post-processors, in registration order.</param>
    public RequestPostProcessorBehavior(IEnumerable<IRequestPostProcessor<TRequest, TResponse>> postProcessors)
    {
        ArgumentNullException.ThrowIfNull(postProcessors);
        _processors = postProcessors as IRequestPostProcessor<TRequest, TResponse>[] ?? [.. postProcessors];
    }

    bool IOptionalBehavior.IsEmpty => _processors.Length == 0;

    /// <inheritdoc />
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);
        TResponse response = await next(cancellationToken).ConfigureAwait(false);
        foreach (IRequestPostProcessor<TRequest, TResponse> processor in _processors)
        {
            await processor.Process(request, response, cancellationToken).ConfigureAwait(false);
        }

        return response;
    }
}
