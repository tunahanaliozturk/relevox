using System.ComponentModel;
using System.Diagnostics;

namespace Relevox.Internal;

/// <summary>
/// A stream whose handler is resolved and called when enumeration starts. Used by generated code.
/// </summary>
/// <remarks>
/// <para>
/// MediatR returns an async iterator that wraps the handler's stream. That gives behaviours worth keeping: the
/// handler is resolved when enumeration starts, so enumerating twice gets a fresh transient handler; the token
/// the handler sees combines the one given to <c>CreateStream</c> with the one given to <c>WithCancellation</c>;
/// and the enumerator has an end state, after which it answers <c>false</c> and nothing more runs. It also
/// costs a second state machine step for every item, about 12 ns each.
/// </para>
/// <para>
/// This keeps those behaviours and drops the per-item cost. The first <c>MoveNextAsync</c> opens the handler's
/// stream inside an async method, so the caller's execution context is restored afterwards and any exception
/// faults the returned task, as in the iterator. Every later call goes straight to the handler's enumerator.
/// The one difference left: from the second item on, an exception a hand-written enumerator throws
/// synchronously from <c>MoveNextAsync</c> reaches the caller as a throw rather than a faulted task. An
/// <c>await foreach</c> cannot tell the two apart.
/// </para>
/// </remarks>
/// <typeparam name="TState">What <c>open</c> needs, normally the generated mediator.</typeparam>
/// <typeparam name="TRequest">The stream request type.</typeparam>
/// <typeparam name="TResponse">The item type.</typeparam>
[EditorBrowsable(EditorBrowsableState.Never)]
[StackTraceHidden, DebuggerNonUserCode, DebuggerStepThrough]
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

    [StackTraceHidden, DebuggerNonUserCode, DebuggerStepThrough]
    private sealed class Enumerator(DeferredEnumerable<TState, TRequest, TResponse> owner, CancellationToken enumeration) : IAsyncEnumerator<TResponse>
    {
        private IAsyncEnumerator<TResponse>? _inner;
        private CancellationTokenSource? _linked;
        private bool _done;

        public TResponse Current => _done || _inner is null ? default! : _inner.Current;

        public ValueTask<bool> MoveNextAsync()
        {
            if (_done)
            {
                return new ValueTask<bool>(false);
            }

            return _inner is null ? OpenAndMoveAsync() : _inner.MoveNextAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (_done && _inner is null && _linked is null)
            {
                return;
            }

            _done = true;
            IAsyncEnumerator<TResponse>? inner = _inner;
            _inner = null;
            try
            {
                if (inner is not null)
                {
                    await inner.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _linked?.Dispose();
                _linked = null;
            }
        }

        private async ValueTask<bool> OpenAndMoveAsync()
        {
            try
            {
                CancellationToken token = Combine(owner._creation, enumeration);
                _inner = owner._open(owner._state, owner._request, token).GetAsyncEnumerator(token);
                return await _inner.MoveNextAsync().ConfigureAwait(false);
            }
            catch
            {
                // A stream that failed to start is finished, as an iterator is after an exception.
                _done = true;
                _linked?.Dispose();
                _linked = null;
                throw;
            }
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
