using Capar;

namespace Benchmarks.CaparPeer;

// The MediatR scenarios, unchanged apart from the using directive: Capar keeps MediatR's handler signatures,
// so the same handler code compiles against both. Cached completed tasks rather than Task.FromResult, so the
// numbers are the cost of getting to the handler and not of the handler.

public sealed record Ping(int Value) : IRequest<int>;

public sealed class PingHandler : IRequestHandler<Ping, int>
{
    private static readonly Task<int> Answer = Task.FromResult(42);

    public Task<int> Handle(Ping request, CancellationToken cancellationToken) => Answer;
}

public sealed record Piped(int Value) : IRequest<int>;

public sealed class PipedHandler : IRequestHandler<Piped, int>
{
    private static readonly Task<int> Answer = Task.FromResult(42);

    public Task<int> Handle(Piped request, CancellationToken cancellationToken) => Answer;
}

public sealed class First : IPipelineBehavior<Piped, int>
{
    public Task<int> Handle(Piped request, RequestHandlerDelegate<int> next, CancellationToken cancellationToken) =>
        next(cancellationToken);
}

public sealed class Second : IPipelineBehavior<Piped, int>
{
    public Task<int> Handle(Piped request, RequestHandlerDelegate<int> next, CancellationToken cancellationToken) =>
        next(cancellationToken);
}

public sealed class Third : IPipelineBehavior<Piped, int>
{
    public Task<int> Handle(Piped request, RequestHandlerDelegate<int> next, CancellationToken cancellationToken)
    {
        Sanity.Count("Capar behaviour");
        return next(cancellationToken);
    }
}

public sealed record OneListener : INotification;

public sealed class OneListenerHandler : INotificationHandler<OneListener>
{
    public Task Handle(OneListener notification, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed record FiveListeners : INotification;

public sealed class FiveListenersA : INotificationHandler<FiveListeners>
{
    public Task Handle(FiveListeners notification, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class FiveListenersB : INotificationHandler<FiveListeners>
{
    public Task Handle(FiveListeners notification, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class FiveListenersC : INotificationHandler<FiveListeners>
{
    public Task Handle(FiveListeners notification, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class FiveListenersD : INotificationHandler<FiveListeners>
{
    public Task Handle(FiveListeners notification, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class FiveListenersE : INotificationHandler<FiveListeners>
{
    public Task Handle(FiveListeners notification, CancellationToken cancellationToken)
    {
        Sanity.Count("Capar fifth listener");
        return Task.CompletedTask;
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
