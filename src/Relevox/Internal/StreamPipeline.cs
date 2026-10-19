using System.ComponentModel;
using System.Diagnostics;

namespace Relevox.Internal;

/// <summary>
/// Runs a stream request through its stream behaviours. Called by generated code; not meant to be called directly.
/// </summary>
/// <remarks>
/// The same shape as <see cref="RequestPipeline"/>: a static handler delegate plus explicit state, one link per
/// behaviour, and a link that can be called more than once. A stream behaviour's <c>next</c> takes no token;
/// every step receives the token the enumeration was started with, as in MediatR.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
[StackTraceHidden, DebuggerNonUserCode, DebuggerStepThrough]
public static class StreamPipeline
{
    /// <summary>Composes the behaviours, outermost first, around the handler's stream.</summary>
    /// <typeparam name="TState">What the handler delegate needs, normally the generated mediator.</typeparam>
    /// <typeparam name="TRequest">The stream request type.</typeparam>
    /// <typeparam name="TResponse">The item type.</typeparam>
    /// <param name="state">Passed to <paramref name="handler"/>.</param>
    /// <param name="request">The request.</param>
    /// <param name="behaviors">The behaviours, in the order the container returned them.</param>
    /// <param name="handler">Resolves the handler and returns its stream.</param>
    /// <param name="cancellationToken">The enumeration's token.</param>
    /// <returns>The outermost behaviour's stream.</returns>
    public static IAsyncEnumerable<TResponse> Run<TState, TRequest, TResponse>(
        TState state,
        TRequest request,
        IStreamPipelineBehavior<TRequest, TResponse>[] behaviors,
        Func<TState, TRequest, CancellationToken, IAsyncEnumerable<TResponse>> handler,
        CancellationToken cancellationToken)
        where TRequest : notnull
    {
        ArgumentNullException.ThrowIfNull(behaviors);
        ArgumentNullException.ThrowIfNull(handler);

        return new Chain<TState, TRequest, TResponse>(state, request, behaviors, handler, cancellationToken).Invoke(0);
    }

    [StackTraceHidden, DebuggerNonUserCode, DebuggerStepThrough]
    private sealed class Chain<TState, TRequest, TResponse>(
        TState state,
        TRequest request,
        IStreamPipelineBehavior<TRequest, TResponse>[] behaviors,
        Func<TState, TRequest, CancellationToken, IAsyncEnumerable<TResponse>> handler,
        CancellationToken cancellationToken)
        where TRequest : notnull
    {
        private readonly StreamHandlerDelegate<TResponse>?[] _links = new StreamHandlerDelegate<TResponse>?[behaviors.Length];

        public IAsyncEnumerable<TResponse> Invoke(int index) =>
            index == behaviors.Length
                ? handler(state, request, cancellationToken)
                : behaviors[index].Handle(request, _links[index] ??= new Next(this, index + 1).Invoke, cancellationToken);

        [StackTraceHidden, DebuggerNonUserCode, DebuggerStepThrough]
        private sealed class Next(Chain<TState, TRequest, TResponse> chain, int index)
        {
            public IAsyncEnumerable<TResponse> Invoke() => chain.Invoke(index);
        }
    }
}
