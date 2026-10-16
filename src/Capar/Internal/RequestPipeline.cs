using System.ComponentModel;

namespace Capar.Internal;

/// <summary>
/// Runs a request through its pipeline behaviours. Called by generated code; not meant to be called directly.
/// </summary>
/// <remarks>
/// <para>
/// The handler is passed as a static delegate plus the mediator as explicit state, so the delegate is
/// allocated once per request type for the life of the process rather than once per call.
/// </para>
/// <para>
/// Each behaviour gets its own <see cref="RequestHandlerDelegate{TResponse}"/>, and that delegate can be
/// called more than once: a retry behaviour calling <c>next</c> a second time runs the rest of the pipeline
/// again, as it does in MediatR. A token passed to <c>next</c> replaces the original from there inward;
/// <c>next()</c> with no token keeps the token <c>Send</c> was given.
/// </para>
/// <para>
/// Per send, the chain object and one delegate per behaviour are allocated, and one more for each step a
/// retrying behaviour runs again. For the first eight behaviours the delegates point at methods on the chain
/// itself, so no separate object is needed to remember which step comes next; that took the three-behaviour
/// send from 544 to 392 bytes.
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
        public Task<TResponse> Invoke(int index, CancellationToken cancellationToken) =>
            index == behaviors.Length
                ? handler(state, request, cancellationToken)
                : behaviors[index].Handle(request, Link(index + 1), cancellationToken);

        private RequestHandlerDelegate<TResponse> Link(int index) => index switch
        {
            1 => Step1,
            2 => Step2,
            3 => Step3,
            4 => Step4,
            5 => Step5,
            6 => Step6,
            7 => Step7,
            8 => Step8,
            _ => new Next(this, index).Invoke,
        };

        private CancellationToken Token(CancellationToken passed) => passed == default ? original : passed;

        private Task<TResponse> Step1(CancellationToken cancellationToken) => Invoke(1, Token(cancellationToken));

        private Task<TResponse> Step2(CancellationToken cancellationToken) => Invoke(2, Token(cancellationToken));

        private Task<TResponse> Step3(CancellationToken cancellationToken) => Invoke(3, Token(cancellationToken));

        private Task<TResponse> Step4(CancellationToken cancellationToken) => Invoke(4, Token(cancellationToken));

        private Task<TResponse> Step5(CancellationToken cancellationToken) => Invoke(5, Token(cancellationToken));

        private Task<TResponse> Step6(CancellationToken cancellationToken) => Invoke(6, Token(cancellationToken));

        private Task<TResponse> Step7(CancellationToken cancellationToken) => Invoke(7, Token(cancellationToken));

        private Task<TResponse> Step8(CancellationToken cancellationToken) => Invoke(8, Token(cancellationToken));

        /// <summary>A step beyond the eighth, which needs an object of its own to remember where it is.</summary>
        private sealed class Next(Chain<TState, TRequest, TResponse> chain, int index)
        {
            public Task<TResponse> Invoke(CancellationToken cancellationToken) =>
                chain.Invoke(index, chain.Token(cancellationToken));
        }
    }
}
