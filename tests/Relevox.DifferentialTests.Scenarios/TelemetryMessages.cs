namespace Scenarios;

// Messages for the telemetry tests, which run on Relevox only. They compile into the MediatR assembly as well,
// where no scenario sends them.

/// <summary>Sends a Ping from its handler after yielding, so the Ping's span is a child of this one.</summary>
public sealed record Relay(string Text) : IRequest<string>;

public sealed class RelayHandler(ISender sender) : IRequestHandler<Relay, string>
{
    public async Task<string> Handle(Relay request, CancellationToken cancellationToken)
    {
        await Task.Yield();
        return await sender.Send(new Ping(request.Text), cancellationToken);
    }
}

/// <summary>Fails after yielding, so the failure arrives through a task that was still running when Send returned.</summary>
public sealed record Stall : IRequest<int>;

public sealed class StallHandler : IRequestHandler<Stall, int>
{
    public async Task<int> Handle(Stall request, CancellationToken cancellationToken)
    {
        await Task.Yield();
        throw new TimeoutException("stalled after a yield");
    }
}

/// <summary>Answers with the name of the activity current while its handler runs.</summary>
public sealed record Probe : IRequest<string>;

public sealed class ProbeHandler : IRequestHandler<Probe, string>
{
    public Task<string> Handle(Probe request, CancellationToken cancellationToken) =>
        Task.FromResult(System.Diagnostics.Activity.Current?.OperationName ?? "none");
}

/// <summary>
/// Two handlers, the second of which faults its task after a yield, so a span per handler has one failure to
/// find among them.
/// </summary>
public sealed record Brownout(string Grid) : INotification;

public sealed class BrownoutLogger(Trace trace) : INotificationHandler<Brownout>
{
    public Task Handle(Brownout notification, CancellationToken cancellationToken)
    {
        trace.Add($"BrownoutLogger({notification.Grid})");
        return Task.CompletedTask;
    }
}

public sealed class BrownoutBreaker(Trace trace) : INotificationHandler<Brownout>
{
    public async Task Handle(Brownout notification, CancellationToken cancellationToken)
    {
        trace.Add($"BrownoutBreaker({notification.Grid})");
        await Task.Yield();
        throw new TimeoutException("tripped after a yield");
    }
}
