using System.ComponentModel;

namespace Capar.Internal;

/// <summary>
/// A stream whose handler is resolved and called when enumeration starts. Used by generated code.
/// </summary>
/// <remarks>
/// <para>
/// MediatR returns an async iterator that wraps the handler's stream. That gives two behaviours worth keeping:
/// the handler is resolved when enumeration starts, so enumerating twice gets a fresh transient handler, and
/// the token the handler sees combines the one given to <c>CreateStream</c> with the one given to
/// <c>WithCancellation</c>. It also costs a second state machine step for every item, about 12 ns each.
/// </para>
/// <para>
/// This keeps both behaviours and drops the per-item cost. The first <c>MoveNextAsync</c> opens the handler's
/// stream with the combined token; every later call goes straight to the handler's own enumerator.
/// </para>
/// </remarks>
/// <typeparam name="TState">What <c>open</c> needs, normally the generated mediator.</typeparam>
/// <typeparam name="TRequest">The stream request type.</typeparam>
/// <typeparam name="TResponse">The item type.</typeparam>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class DeferredEnumerable<TState, TRequest, TResponse> : IAsyncEnumerable<TResponse>
{
    private readonly TState _state;
    private readonly TRequest _request;
    private readonly Func<TState, TRequest, CancellationToken, IAsyncEnumerable<TResponse>> _open;
    private readonly CancellationToken _creation;

    /// <summary>Creates the stream without starting it.</summary>
    /// <param name="state">Passed to <paramref name="open"/>.</param>
    /// <param name="request">The request.</param>
    /// <param name="open">Resolves the behaviours and the handler and returns the stream to enumerate.</param>
    /// <param name="cancellationToken">The token given to <c>CreateStream</c>.</param>
    public DeferredEnumerable(
        TState state,
        TRequest request,
        Func<TState, TRequest, CancellationToken, IAsyncEnumerable<TResponse>> open,
        CancellationToken cancellationToken)
    {
        _state = state;
        _request = request;
        _open = open;
        _creation = cancellationToken;
    }

    /// <inheritdoc />
    public IAsyncEnumerator<TResponse> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        new Enumerator(this, cancellationToken);

    private sealed class Enumerator(DeferredEnumerable<TState, TRequest, TResponse> owner, CancellationToken enumeration) : IAsyncEnumerator<TResponse>
    {
        private IAsyncEnumerator<TResponse>? _inner;
        private CancellationTokenSource? _linked;

        public TResponse Current => _inner is null ? default! : _inner.Current;

        public ValueTask<bool> MoveNextAsync()
        {
            if (_inner is not null)
            {
                return _inner.MoveNextAsync();
            }

            // A handler that throws while producing its stream faults the first MoveNextAsync rather than
            // throwing from it, as an async iterator does.
            try
            {
                CancellationToken token = Combine(owner._creation, enumeration);
                _inner = owner._open(owner._state, owner._request, token).GetAsyncEnumerator(token);
                return _inner.MoveNextAsync();
            }
            catch (Exception exception)
            {
                return ValueTask.FromException<bool>(exception);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_inner is not null)
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
            }

            _linked?.Dispose();
        }

        /// <summary>Combines the two tokens the way the compiler does for an [EnumeratorCancellation] parameter.</summary>
        private CancellationToken Combine(CancellationToken creation, CancellationToken enumeration)
        {
            if (!enumeration.CanBeCanceled || enumeration == creation)
            {
                return creation;
            }

            if (!creation.CanBeCanceled)
            {
                return enumeration;
            }

            _linked = CancellationTokenSource.CreateLinkedTokenSource(creation, enumeration);
            return _linked.Token;
        }
    }
}
