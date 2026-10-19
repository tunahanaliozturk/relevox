using Mediator;

namespace Benchmarks.MediatorPeer;

// The same scenarios written against martinothamar/Mediator, in its own idiom: ValueTask everywhere, so a
// constant result costs nothing.

public sealed record Ping(int Value) : IRequest<int>;

public sealed class PingHandler : IRequestHandler<Ping, int>
{
    public ValueTask<int> Handle(Ping request, CancellationToken cancellationToken) => new(42);
}

public sealed record Piped(int Value) : IRequest<int>;

public sealed class PipedHandler : IRequestHandler<Piped, int>
{
    public ValueTask<int> Handle(Piped request, CancellationToken cancellationToken) => new(42);
}

public sealed class First : IPipelineBehavior<Piped, int>
{
    public ValueTask<int> Handle(Piped message, MessageHandlerDelegate<Piped, int> next, CancellationToken cancellationToken) =>
        next(message, cancellationToken);
}

public sealed class Second : IPipelineBehavior<Piped, int>
{
    public ValueTask<int> Handle(Piped message, MessageHandlerDelegate<Piped, int> next, CancellationToken cancellationToken) =>
        next(message, cancellationToken);
}

public sealed class Third : IPipelineBehavior<Piped, int>
{
    public ValueTask<int> Handle(Piped message, MessageHandlerDelegate<Piped, int> next, CancellationToken cancellationToken)
    {
        Sanity.Count("Mediator behaviour");
        return next(message, cancellationToken);
    }
}

public sealed record OneListener : INotification;

public sealed class OneListenerHandler : INotificationHandler<OneListener>
{
    public ValueTask Handle(OneListener notification, CancellationToken cancellationToken) => default;
}

public sealed record FiveListeners : INotification;

public sealed class FiveListenersA : INotificationHandler<FiveListeners>
{
    public ValueTask Handle(FiveListeners notification, CancellationToken cancellationToken) => default;
}

public sealed class FiveListenersB : INotificationHandler<FiveListeners>
{
    public ValueTask Handle(FiveListeners notification, CancellationToken cancellationToken) => default;
}

public sealed class FiveListenersC : INotificationHandler<FiveListeners>
{
    public ValueTask Handle(FiveListeners notification, CancellationToken cancellationToken) => default;
}

public sealed class FiveListenersD : INotificationHandler<FiveListeners>
{
    public ValueTask Handle(FiveListeners notification, CancellationToken cancellationToken) => default;
}

public sealed class FiveListenersE : INotificationHandler<FiveListeners>
{
    public ValueTask Handle(FiveListeners notification, CancellationToken cancellationToken)
    {
        Sanity.Count("Mediator fifth listener");
        return default;
    }
}

public sealed record Count(int Items) : IStreamRequest<int>;

public sealed class CountHandler : IStreamRequestHandler<Count, int>
{
    public async IAsyncEnumerable<int> Handle(
        Count request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (int i = 0; i < request.Items; i++)
        {
            yield return i;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }
}
