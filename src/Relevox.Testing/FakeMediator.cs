using System.Runtime.CompilerServices;

namespace Relevox.Testing;

/// <summary>
/// An <see cref="IMediator"/> for unit tests: it answers each request with what the test set up, and records
/// every request sent and every notification published, so the test can check them afterwards.
/// </summary>
/// <remarks>
/// <para>
/// No handler, behaviour or container is involved. A request nobody set up fails, with a message naming it,
/// rather than quietly returning a default value the code under test would carry on with.
/// </para>
/// <para>
/// Setups are matched on the request's runtime type, the way the generated mediator dispatches. Calls are safe
/// from several threads.
/// </para>
/// </remarks>
public sealed class FakeMediator : IMediator
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Type, Func<object, CancellationToken, Task<object?>>> _requests = [];
    private readonly Dictionary<Type, Func<object, CancellationToken, IAsyncEnumerable<object?>>> _streams = [];
    private readonly List<object> _sent = [];
    private readonly List<object> _published = [];

    /// <summary>Answers every <typeparamref name="TRequest"/> with what <paramref name="respond"/> returns.</summary>
    /// <typeparam name="TRequest">The request type.</typeparam>
    /// <typeparam name="TResponse">The response type.</typeparam>
    /// <param name="respond">Works out the response from the request.</param>
    /// <returns>This fake, to chain further setups.</returns>
    public FakeMediator Setup<TRequest, TResponse>(Func<TRequest, TResponse> respond)
        where TRequest : IRequest<TResponse>
    {
        ArgumentNullException.ThrowIfNull(respond);
        return Add(typeof(TRequest), (request, _) => Task.FromResult<object?>(respond((TRequest)request)));
    }

    /// <summary>Answers every <typeparamref name="TRequest"/> with the task <paramref name="respond"/> returns.</summary>
    /// <typeparam name="TRequest">The request type.</typeparam>
    /// <typeparam name="TResponse">The response type.</typeparam>
    /// <param name="respond">Works out the response from the request and the send's cancellation token.</param>
    /// <returns>This fake, to chain further setups.</returns>
    public FakeMediator SetupAsync<TRequest, TResponse>(Func<TRequest, CancellationToken, Task<TResponse>> respond)
        where TRequest : IRequest<TResponse>
    {
        ArgumentNullException.ThrowIfNull(respond);
        return Add(typeof(TRequest), async (request, token) => await respond((TRequest)request, token).ConfigureAwait(false));
    }

    /// <summary>Handles every <typeparamref name="TRequest"/>, a request without a response, with <paramref name="handle"/>.</summary>
    /// <typeparam name="TRequest">The request type.</typeparam>
    /// <param name="handle">Runs for each request. Write it with braces, <c>r =&gt; { ... }</c>, so it cannot be read as returning a value.</param>
    /// <returns>This fake, to chain further setups.</returns>
    public FakeMediator Setup<TRequest>(Action<TRequest> handle)
        where TRequest : IRequest
    {
        ArgumentNullException.ThrowIfNull(handle);
        return Add(typeof(TRequest), (request, _) =>
        {
            handle((TRequest)request);
            return Task.FromResult<object?>(Unit.Value);
        });
    }

    /// <summary>Fails every <typeparamref name="TRequest"/> with <paramref name="exception"/>, as a faulted task.</summary>
    /// <typeparam name="TRequest">The request or stream request type.</typeparam>
    /// <param name="exception">The exception the send fails with.</param>
    /// <returns>This fake, to chain further setups.</returns>
    public FakeMediator Throws<TRequest>(Exception exception)
        where TRequest : notnull
    {
        ArgumentNullException.ThrowIfNull(exception);
        lock (_gate)
        {
            _requests[typeof(TRequest)] = (_, _) => Task.FromException<object?>(exception);
            _streams[typeof(TRequest)] = (_, _) => Failing(exception);
        }

        return this;
    }

    /// <summary>Answers every <typeparamref name="TRequest"/> stream with <paramref name="items"/>.</summary>
    /// <typeparam name="TRequest">The stream request type.</typeparam>
    /// <typeparam name="TItem">The item type.</typeparam>
    /// <param name="items">Works out the items from the request.</param>
    /// <returns>This fake, to chain further setups.</returns>
    public FakeMediator SetupStream<TRequest, TItem>(Func<TRequest, IEnumerable<TItem>> items)
        where TRequest : IStreamRequest<TItem>
    {
        ArgumentNullException.ThrowIfNull(items);
        lock (_gate)
        {
            _streams[typeof(TRequest)] = (request, token) => Yield(items((TRequest)request), token);
        }

        return this;
    }

    /// <summary>The requests sent so far, stream requests included, that are a <typeparamref name="TRequest"/>, in order.</summary>
    /// <typeparam name="TRequest">The request type, or <see cref="object"/> for all of them.</typeparam>
    /// <returns>A copy; later sends do not change it.</returns>
    public IReadOnlyList<TRequest> Sent<TRequest>()
    {
        lock (_gate)
        {
            return [.. _sent.OfType<TRequest>()];
        }
    }

    /// <summary>The notifications published so far that are a <typeparamref name="TNotification"/>, in order.</summary>
    /// <typeparam name="TNotification">The notification type, or <see cref="object"/> for all of them.</typeparam>
    /// <returns>A copy; later publishes do not change it.</returns>
    public IReadOnlyList<TNotification> Published<TNotification>()
    {
        lock (_gate)
        {
            return [.. _published.OfType<TNotification>()];
        }
    }

    /// <summary>Forgets what was sent and published. Setups stay.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _sent.Clear();
            _published.Clear();
        }
    }

    /// <inheritdoc/>
    public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
        (TResponse)(await Dispatch(request, cancellationToken).ConfigureAwait(false))!;

    /// <inheritdoc/>
    public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
        where TRequest : IRequest =>
        Dispatch(request, cancellationToken);

    /// <inheritdoc/>
    public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
        Dispatch(request, cancellationToken);

    /// <inheritdoc/>
    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
        Cast<TResponse>(Stream(request, cancellationToken));

    /// <inheritdoc/>
    public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
        Stream(request, cancellationToken);

    /// <inheritdoc/>
    public Task Publish(object notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        lock (_gate)
        {
            _published.Add(notification);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification =>
        Publish((object)notification, cancellationToken);

    private FakeMediator Add(Type request, Func<object, CancellationToken, Task<object?>> respond)
    {
        lock (_gate)
        {
            _requests[request] = respond;
        }

        return this;
    }

    private Task<object?> Dispatch(object request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Func<object, CancellationToken, Task<object?>>? respond;
        lock (_gate)
        {
            _sent.Add(request);
            _requests.TryGetValue(request.GetType(), out respond);
        }

        if (respond is null)
        {
            return Task.FromException<object?>(NotSetUp(request, nameof(Setup)));
        }

        try
        {
            return respond(request, cancellationToken);
        }
        catch (Exception exception)
        {
            return Task.FromException<object?>(exception);
        }
    }

    private IAsyncEnumerable<object?> Stream(object request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Func<object, CancellationToken, IAsyncEnumerable<object?>>? items;
        lock (_gate)
        {
            _sent.Add(request);
            _streams.TryGetValue(request.GetType(), out items);
        }

        return items is null ? Failing(NotSetUp(request, nameof(SetupStream))) : items(request, cancellationToken);
    }

    private static InvalidOperationException NotSetUp(object request, string method) =>
        new($"The fake mediator has no setup for '{request.GetType()}'. Call {nameof(FakeMediator)}.{method} for it before the code under test sends it.");

    private static async IAsyncEnumerable<object?> Yield<T>(IEnumerable<T> items, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (T item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return item;
        }
    }

    private static async IAsyncEnumerable<T> Cast<T>(IAsyncEnumerable<object?> items)
    {
        await foreach (object? item in items.ConfigureAwait(false))
        {
            yield return (T)item!;
        }
    }

    // A stream fails when it is enumerated, not when it is created, as the generated mediator's does.
    private static async IAsyncEnumerable<object?> Failing(Exception exception)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        if (exception is not null)
        {
            throw exception;
        }

        yield break;
    }
}
