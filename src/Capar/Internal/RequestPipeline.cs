using System.ComponentModel;

namespace Capar.Internal;

/// <summary>
/// Runs a request through its pipeline behaviours. Called by generated code; not meant to be called directly.
/// </summary>
/// <remarks>
/// <para>
/// The handler is passed as a static delegate plus the mediator as explicit state, so the delegate is
/// allocated once per request type for the life of the process rather than once per call. The chain object
/// and one link per behaviour are allocated per call, and only when the request has behaviours at all.
/// </para>
/// <para>
/// Each behaviour gets its own <see cref="RequestHandlerDelegate{TResponse}"/>, and that delegate can be
/// called more than once: a retry behaviour calling <c>next</c> a second time runs the rest of the pipeline
/// again, as it does in MediatR. A token passed to <c>next</c> replaces the original from there inward;
/// <c>next()</c> with no token keeps the token <c>Send</c> was given.
/// </para>
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class RequestPipeline
{
    /// <summary>Runs the behaviours, outermost first, and then the handler.</summary>
    /// <typeparam name="TState">What the handler delegate needs, normally the generated mediator.</typeparam>
    /// <typeparam name="TRequest">The request type.</typeparam>
    /// <typeparam name="TResponse">The response type.</typeparam>
    /// <param name="state">Passed to <paramref name="handler"/>.</param>
    /// <param name="request">The request.</param>
    /// <param name="behaviors">The behaviours, in the order the container returned them.</param>
    /// <param name="handler">Resolves and calls the handler.</param>
    /// <param name="cancellationToken">The token <c>Send</c> was given.</param>
    /// <returns>The response.</returns>
    public static Task<TResponse> Run<TState, TRequest, TResponse>(
        TState state,
        TRequest request,
        IPipelineBehavior<TRequest, TResponse>[] behaviors,
        Func<TState, TRequest, CancellationToken, Task<TResponse>> handler,
        CancellationToken cancellationToken)
        where TRequest : notnull
    {
        ArgumentNullException.ThrowIfNull(behaviors);
        ArgumentNullException.ThrowIfNull(handler);

        return behaviors.Length == 0
            ? handler(state, request, cancellationToken)
            : new Chain<TState, TRequest, TResponse>(state, request, behaviors, handler, cancellationToken).Invoke(0, cancellationToken);
    }

    private sealed class Chain<TState, TRequest, TResponse>(
        TState state,
        TRequest request,
        IPipelineBehavior<TRequest, TResponse>[] behaviors,
        Func<TState, TRequest, CancellationToken, Task<TResponse>> handler,
        CancellationToken original)
        where TRequest : notnull
    {
        private readonly RequestHandlerDelegate<TResponse>?[] _links = new RequestHandlerDelegate<TResponse>?[behaviors.Length];

        public Task<TResponse> Invoke(int index, CancellationToken cancellationToken) =>
            index == behaviors.Length
                ? handler(state, request, cancellationToken)
                : behaviors[index].Handle(request, Link(index + 1), cancellationToken);

        private RequestHandlerDelegate<TResponse> Link(int index) =>
            _links[index - 1] ??= new Next(this, index).Invoke;

        private sealed class Next(Chain<TState, TRequest, TResponse> chain, int index)
        {
            public Task<TResponse> Invoke(CancellationToken cancellationToken) =>
                chain.Invoke(index, cancellationToken == default ? chain.Original : cancellationToken);
        }

        private CancellationToken Original => original;
    }
}
