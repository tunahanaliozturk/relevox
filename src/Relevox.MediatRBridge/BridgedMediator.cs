using System.Collections.Concurrent;

namespace Relevox.MediatRBridge;

/// <summary>
/// MediatR's mediator interfaces, answered by Relevox for the messages that have moved and by MediatR for the rest.
/// </summary>
/// <remarks>
/// A message has moved when its type implements Relevox's interface. That is a decision the compiler can check:
/// RVX0001 warns about a Relevox request with no Relevox handler, so a request whose handler has not moved is
/// reported at build time rather than sent to a library that cannot handle it. Each request goes to one mediator, through that mediator's pipeline only, so a behaviour
/// registered with both runs once. See ADR 0022.
/// </remarks>
internal sealed class BridgedMediator(MediatR.IMediator mediatr, IMediator relevox) : MediatR.IMediator
{
    private static readonly ConcurrentDictionary<Type, bool> RelevoxStreams = new();

    public Task<TResponse> Send<TResponse>(MediatR.IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
        request is IRequest<TResponse> moved
            ? relevox.Send(moved, cancellationToken)
            : mediatr.Send(request, cancellationToken);

    public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
        where TRequest : MediatR.IRequest =>
        request is IRequest moved
            ? relevox.Send(moved, cancellationToken)
            : mediatr.Send(request, cancellationToken);

    public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
        request is IBaseRequest
            ? SendMoved(request, cancellationToken)
            : mediatr.Send(request, cancellationToken);

    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(MediatR.IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
        request is IStreamRequest<TResponse> moved
            ? relevox.CreateStream(moved, cancellationToken)
            : mediatr.CreateStream(request, cancellationToken);

    public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
        request is not null && IsRelevoxStream(request.GetType())
            ? relevox.CreateStream(request, cancellationToken)
            : mediatr.CreateStream(request!, cancellationToken);

    public Task Publish(object notification, CancellationToken cancellationToken = default) => notification switch
    {
        MediatR.INotification and INotification => PublishBoth(notification, cancellationToken),
        INotification => relevox.Publish(notification, cancellationToken),
        _ => mediatr.Publish(notification, cancellationToken),
    };

    public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : MediatR.INotification =>
        notification is INotification
            ? PublishBoth(notification, cancellationToken)
            : mediatr.Publish(notification, cancellationToken);

    // A caller on MediatR's interface expects MediatR's Unit from a request with no response, as MediatR returns.
    private async Task<object?> SendMoved(object request, CancellationToken cancellationToken)
    {
        object? response = await relevox.Send(request, cancellationToken).ConfigureAwait(false);
        return response is Unit ? MediatR.Unit.Value : response;
    }

    // MediatR's handlers first, as they were there first, then Relevox's. With MediatR's default publisher a handler
    // that throws stops the rest, and that includes the Relevox side.
    // Both libraries dispatch on the runtime type, so the untyped overloads reach the same handlers as typed ones.
    private async Task PublishBoth(object notification, CancellationToken cancellationToken)
    {
        await mediatr.Publish(notification, cancellationToken).ConfigureAwait(false);
        await relevox.Publish(notification, cancellationToken).ConfigureAwait(false);
    }

    // Relevox's stream request has no non-generic base to test with "is", so the interfaces are read once per type.
    private static bool IsRelevoxStream(Type type) =>
        RelevoxStreams.GetOrAdd(
            type,
            static type => type.GetInterfaces().Any(static contract =>
                contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IStreamRequest<>)));
}
