namespace Relevox.Pipeline;

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
    /// <remarks>
    /// Behaves as an <c>async</c> method, as MediatR's does: anything thrown ends up in the returned task, and what the
    /// rest of the pipeline changes in the execution context, such as an <see cref="AsyncLocal{T}"/> or the current
    /// culture, does not reach the caller. While the processors finish synchronously, no state machine is built and
    /// the rest of the pipeline's task is returned as it is.
    /// </remarks>
    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (!AsyncScope.TryEnter(out AsyncScope scope))
        {
            return Rest(Task.CompletedTask, 0, request, next, cancellationToken);
        }

        try
        {
            ArgumentNullException.ThrowIfNull(next);
            for (int index = 0; index < _processors.Length; index++)
            {
                Task processed = _processors[index].Process(request, cancellationToken);
                if (!processed.IsCompletedSuccessfully)
                {
                    return Rest(processed, index + 1, request, next, cancellationToken);
                }
            }

            // Only a task that succeeded can be handed back as it is. Awaiting one that failed turns an
            // OperationCanceledException fault into a canceled task, as MediatR's await does.
            Task<TResponse> rest = next(cancellationToken);
            return rest.IsCompletedSuccessfully ? rest : Await(rest);
        }
        catch (Exception exception)
        {
            return Faults.Of<TResponse>(exception);
        }
        finally
        {
            scope.Exit();
        }
    }

    private static async Task<TResponse> Await(Task<TResponse> rest) => await rest.ConfigureAwait(false);

    private async Task<TResponse> Rest(Task processed, int index, TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);
        await processed.ConfigureAwait(false);
        for (; index < _processors.Length; index++)
        {
            await _processors[index].Process(request, cancellationToken).ConfigureAwait(false);
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
    /// <remarks>
    /// Behaves as an <c>async</c> method, as MediatR's does: anything thrown ends up in the returned task, and what the
    /// rest of the pipeline changes in the execution context does not reach the caller. While the rest of the
    /// pipeline and the processors finish synchronously, no state machine is built and the pipeline's own completed
    /// task is returned, since the processors cannot change the response.
    /// </remarks>
    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (!AsyncScope.TryEnter(out AsyncScope scope))
        {
            return Slow(request, next, cancellationToken);
        }

        try
        {
            ArgumentNullException.ThrowIfNull(next);
            Task<TResponse> responding = next(cancellationToken);
            if (!responding.IsCompletedSuccessfully)
            {
                return Rest(responding, request, cancellationToken);
            }

            TResponse response = responding.Result;
            for (int index = 0; index < _processors.Length; index++)
            {
                Task processed = _processors[index].Process(request, response, cancellationToken);
                if (!processed.IsCompletedSuccessfully)
                {
                    return After(processed, index + 1, request, response, cancellationToken);
                }
            }

            return responding;
        }
        catch (Exception exception)
        {
            return Faults.Of<TResponse>(exception);
        }
        finally
        {
            scope.Exit();
        }
    }

    private async Task<TResponse> Slow(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);
        TResponse response = await next(cancellationToken).ConfigureAwait(false);
        return await After(Task.CompletedTask, 0, request, response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TResponse> Rest(Task<TResponse> responding, TRequest request, CancellationToken cancellationToken)
    {
        TResponse response = await responding.ConfigureAwait(false);
        return await After(Task.CompletedTask, 0, request, response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TResponse> After(Task processed, int index, TRequest request, TResponse response, CancellationToken cancellationToken)
    {
        await processed.ConfigureAwait(false);
        for (; index < _processors.Length; index++)
        {
            await _processors[index].Process(request, response, cancellationToken).ConfigureAwait(false);
        }

        return response;
    }
}

/// <summary>What an <c>async</c> method returns when its body throws, for methods that avoid being one on their fast path.</summary>
internal static class Faults
{
    /// <summary>
    /// A faulted task, or a canceled one for <see cref="OperationCanceledException"/>, holding the exception with its
    /// original stack trace, exactly as an <c>async</c> method's builder would complete it.
    /// </summary>
    public static Task<T> Of<T>(Exception exception) => Rethrow<T>(System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception));

    private static async Task<T> Rethrow<T>(System.Runtime.ExceptionServices.ExceptionDispatchInfo exception)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        exception.Throw();
        return default!;
    }
}

/// <summary>
/// What an <c>async</c> method's builder does around its body, for methods that avoid being one on their fast path:
/// whatever the body changes in the execution context or the synchronisation context is put back before returning.
/// </summary>
internal readonly struct AsyncScope
{
    private readonly ExecutionContext _context;
    private readonly SynchronizationContext? _synchronization;

    private AsyncScope(ExecutionContext context, SynchronizationContext? synchronization)
    {
        _context = context;
        _synchronization = synchronization;
    }

    /// <summary>
    /// Remembers the current contexts. False while execution context flow is suppressed, which leaves nothing to
    /// put back with; the caller then takes its <c>async</c> path, which still can.
    /// </summary>
    public static bool TryEnter(out AsyncScope scope)
    {
        ExecutionContext? context = ExecutionContext.Capture();
        scope = context is null ? default : new AsyncScope(context, SynchronizationContext.Current);
        return context is not null;
    }

    /// <summary>Puts back whatever changed since <see cref="TryEnter"/>.</summary>
    public void Exit()
    {
        if (!ReferenceEquals(ExecutionContext.Capture(), _context))
        {
            ExecutionContext.Restore(_context);
        }

        if (!ReferenceEquals(SynchronizationContext.Current, _synchronization))
        {
            SynchronizationContext.SetSynchronizationContext(_synchronization);
        }
    }
}
