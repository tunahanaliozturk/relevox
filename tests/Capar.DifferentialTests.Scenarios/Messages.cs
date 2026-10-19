namespace Scenarios;

// Requests

public sealed record Ping(string Text) : IRequest<string>;

public sealed class PingHandler(Trace trace) : IRequestHandler<Ping, string>
{
    public Task<string> Handle(Ping request, CancellationToken cancellationToken)
    {
        trace.Add($"PingHandler({request.Text})");
        return Task.FromResult(request.Text + " pong");
    }
}

public sealed record Ring(int Times) : IRequest;

public sealed class RingHandler(Trace trace) : IRequestHandler<Ring>
{
    public Task Handle(Ring request, CancellationToken cancellationToken)
    {
        trace.Add($"RingHandler({request.Times})");
        return Task.CompletedTask;
    }
}

/// <summary>Nothing handles this. The generator warns about it; the build suppresses that on purpose.</summary>
public sealed record Orphan : IRequest<int>;

public record BaseQuery : IRequest<string>;

/// <summary>Has no handler of its own, only its base does.</summary>
public sealed record DerivedQuery : BaseQuery;

public sealed class BaseQueryHandler(Trace trace) : IRequestHandler<BaseQuery, string>
{
    public Task<string> Handle(BaseQuery request, CancellationToken cancellationToken)
    {
        trace.Add($"BaseQueryHandler({request.GetType().Name})");
        return Task.FromResult("base");
    }
}

public sealed record Explode : IRequest<int>;

public sealed class ExplodeHandler(Trace trace) : IRequestHandler<Explode, int>
{
    public Task<int> Handle(Explode request, CancellationToken cancellationToken)
    {
        trace.Add("ExplodeHandler");
        throw new NotSupportedException("thrown before any await");
    }
}

public sealed record Fizzle : IRequest;

public sealed class FizzleHandler : IRequestHandler<Fizzle>
{
    public Task Handle(Fizzle request, CancellationToken cancellationToken) =>
        throw new NotSupportedException("a request with no response");
}

public sealed record Stamp : IRequest<int>;

public sealed class StampHandler(Trace trace) : IRequestHandler<Stamp, int>
{
    private static int s_instances;
    private readonly int _instance = Interlocked.Increment(ref s_instances);

    public Task<int> Handle(Stamp request, CancellationToken cancellationToken)
    {
        trace.Add("StampHandler");
        return Task.FromResult(_instance);
    }
}

public sealed record TokenCheck : IRequest<bool>;

public sealed class TokenCheckHandler(Trace trace) : IRequestHandler<TokenCheck, bool>
{
    public Task<bool> Handle(TokenCheck request, CancellationToken cancellationToken)
    {
        trace.Add($"TokenCheckHandler(canBeCanceled={cancellationToken.CanBeCanceled})");
        return Task.FromResult(cancellationToken.CanBeCanceled);
    }
}

// Notifications

public abstract record Signal : INotification;

public sealed record Alarm(string Zone) : Signal;

public sealed record Quiet : INotification;

public sealed record Boom : INotification;

public sealed class AlarmLogger(Trace trace) : INotificationHandler<Alarm>
{
    public Task Handle(Alarm notification, CancellationToken cancellationToken)
    {
        trace.Add($"AlarmLogger({notification.Zone})");
        return Task.CompletedTask;
    }
}

public sealed class AlarmPager(Trace trace) : INotificationHandler<Alarm>
{
    public Task Handle(Alarm notification, CancellationToken cancellationToken)
    {
        trace.Add($"AlarmPager({notification.Zone})");
        return Task.CompletedTask;
    }
}

/// <summary>Handles the abstract base, so whether it sees an Alarm is the polymorphism question.</summary>
public sealed class SignalAuditor(Trace trace) : INotificationHandler<Signal>
{
    public Task Handle(Signal notification, CancellationToken cancellationToken)
    {
        trace.Add($"SignalAuditor({notification.GetType().Name})");
        return Task.CompletedTask;
    }
}

/// <summary>Handles INotification itself.</summary>
public sealed class EverythingWatcher(Trace trace) : INotificationHandler<INotification>
{
    public Task Handle(INotification notification, CancellationToken cancellationToken)
    {
        trace.Add($"EverythingWatcher({notification.GetType().Name})");
        return Task.CompletedTask;
    }
}

public sealed class BoomHandler(Trace trace) : INotificationHandler<Boom>
{
    public Task Handle(Boom notification, CancellationToken cancellationToken)
    {
        trace.Add("BoomHandler");
        throw new NotSupportedException("thrown before any await");
    }
}

// Streams

public sealed record Countdown(int From) : IStreamRequest<int>;

public sealed class CountdownHandler(Trace trace) : IStreamRequestHandler<Countdown, int>
{
    public async IAsyncEnumerable<int> Handle(
        Countdown request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        trace.Add($"CountdownHandler({request.From})");
        for (int value = request.From; value > 0; value--)
        {
            yield return value;
        }

        await Task.CompletedTask;
    }
}

/// <summary>A stream request nothing handles.</summary>
public sealed record Silence : IStreamRequest<int>;
