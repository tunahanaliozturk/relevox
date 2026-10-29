using System.Runtime.CompilerServices;

// Outside the Relevox namespace on purpose, so every interface below names its library.
namespace BridgeTests;

/// <summary>What ran, in order, shared by every handler and behaviour in a test.</summary>
public sealed class Trace
{
    public List<string> Events { get; } = [];
}

// Not moved: MediatR's interfaces and a MediatR handler.
public sealed record Legacy(string Text) : MediatR.IRequest<string>;

public sealed class LegacyHandler(Trace trace) : MediatR.IRequestHandler<Legacy, string>
{
    public Task<string> Handle(Legacy request, CancellationToken cancellationToken)
    {
        trace.Events.Add("MediatR handler");
        return Task.FromResult($"mediatr:{request.Text}");
    }
}

// Moved: both libraries' interfaces, so callers on MediatR's ISender still compile, and a Relevox handler.
public sealed record Moved(string Text) : MediatR.IRequest<string>, Relevox.IRequest<string>;

public sealed class MovedHandler(Trace trace) : Relevox.IRequestHandler<Moved, string>
{
    public Task<string> Handle(Moved request, CancellationToken cancellationToken)
    {
        trace.Events.Add("Relevox handler");
        return Task.FromResult($"relevox:{request.Text}");
    }
}

public sealed record LegacyCommand : MediatR.IRequest;

public sealed class LegacyCommandHandler(Trace trace) : MediatR.IRequestHandler<LegacyCommand>
{
    public Task Handle(LegacyCommand request, CancellationToken cancellationToken)
    {
        trace.Events.Add("MediatR command");
        return Task.CompletedTask;
    }
}

public sealed record MovedCommand : MediatR.IRequest, Relevox.IRequest;

public sealed class MovedCommandHandler(Trace trace) : Relevox.IRequestHandler<MovedCommand>
{
    public Task Handle(MovedCommand request, CancellationToken cancellationToken)
    {
        trace.Events.Add($"Relevox command(canBeCanceled={cancellationToken.CanBeCanceled})");
        return Task.CompletedTask;
    }
}

public sealed record LegacyCount(int To) : MediatR.IStreamRequest<int>;

public sealed class LegacyCountHandler : MediatR.IStreamRequestHandler<LegacyCount, int>
{
    public async IAsyncEnumerable<int> Handle(LegacyCount request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (int i = 1; i <= request.To; i++)
        {
            await Task.Yield();
            yield return -i;
        }
    }
}

public sealed record MovedCount(int To) : MediatR.IStreamRequest<int>, Relevox.IStreamRequest<int>;

public sealed class MovedCountHandler : Relevox.IStreamRequestHandler<MovedCount, int>
{
    public async IAsyncEnumerable<int> Handle(MovedCount request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (int i = 1; i <= request.To; i++)
        {
            await Task.Yield();
            yield return i;
        }
    }
}

public sealed record LegacyEvent : MediatR.INotification;

public sealed class OnLegacyEvent(Trace trace) : MediatR.INotificationHandler<LegacyEvent>
{
    public Task Handle(LegacyEvent notification, CancellationToken cancellationToken)
    {
        trace.Events.Add("MediatR LegacyEvent");
        return Task.CompletedTask;
    }
}

public sealed record MovedEvent : MediatR.INotification, Relevox.INotification;

public sealed class OnMovedEvent(Trace trace) : Relevox.INotificationHandler<MovedEvent>
{
    public Task Handle(MovedEvent notification, CancellationToken cancellationToken)
    {
        trace.Events.Add("Relevox MovedEvent");
        return Task.CompletedTask;
    }
}

// Halfway: one handler moved, one not.
public sealed record SharedEvent(bool Fail = false) : MediatR.INotification, Relevox.INotification;

public sealed class OnSharedEventInMediatR(Trace trace) : MediatR.INotificationHandler<SharedEvent>
{
    public Task Handle(SharedEvent notification, CancellationToken cancellationToken)
    {
        trace.Events.Add("MediatR SharedEvent");
        return notification.Fail ? throw new InvalidOperationException("MediatR handler failed") : Task.CompletedTask;
    }
}

public sealed class OnSharedEventInRelevox(Trace trace) : Relevox.INotificationHandler<SharedEvent>
{
    public Task Handle(SharedEvent notification, CancellationToken cancellationToken)
    {
        trace.Events.Add("Relevox SharedEvent");
        return Task.CompletedTask;
    }
}

/// <summary>A behaviour moved by implementing both interfaces, registered with both libraries.</summary>
public sealed class Logged<TRequest, TResponse>(Trace trace)
    : MediatR.IPipelineBehavior<TRequest, TResponse>, Relevox.IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public Task<TResponse> Handle(TRequest request, MediatR.RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        trace.Events.Add($"MediatR behaviour {typeof(TRequest).Name}");
        return next(cancellationToken);
    }

    public Task<TResponse> Handle(TRequest request, Relevox.RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        trace.Events.Add($"Relevox behaviour {typeof(TRequest).Name}");
        return next(cancellationToken);
    }
}
