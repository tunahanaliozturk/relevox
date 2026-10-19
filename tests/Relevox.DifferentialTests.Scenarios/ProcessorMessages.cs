namespace Scenarios;

// Pre and post processors, used by Profile.Pipeline. It turns on AutoRegisterRequestProcessors and adds one
// open pre-processor and one post-processor through the configuration. In MediatR 12 only that explicit
// registration makes the processor behaviours exist at all; the auto-registered processors then run because
// the behaviours resolve every processor the container has.

public sealed class PingPre(Trace trace) : IRequestPreProcessor<Ping>
{
    public Task Process(Ping request, CancellationToken cancellationToken)
    {
        trace.Add($"PingPre({request.Text})");
        return Task.CompletedTask;
    }
}

public sealed class PingPost(Trace trace) : IRequestPostProcessor<Ping, string>
{
    public Task Process(Ping request, string response, CancellationToken cancellationToken)
    {
        trace.Add($"PingPost({response})");
        return Task.CompletedTask;
    }
}

/// <summary>A post-processor for a request that returns nothing, which MediatR 12 types over Unit.</summary>
public sealed class RingPost(Trace trace) : IRequestPostProcessor<Ring, Unit>
{
    public Task Process(Ring request, Unit response, CancellationToken cancellationToken)
    {
        trace.Add($"RingPost({response})");
        return Task.CompletedTask;
    }
}

/// <summary>Open generic: applies to every request.</summary>
public sealed class StampPre<TRequest>(Trace trace) : IRequestPreProcessor<TRequest>
    where TRequest : notnull
{
    public Task Process(TRequest request, CancellationToken cancellationToken)
    {
        trace.Add($"StampPre({typeof(TRequest).Name})");
        return Task.CompletedTask;
    }
}

/// <summary>Written for an interface rather than a request type, to see which requests it reaches.</summary>
public sealed class AuditedPre(Trace trace) : IRequestPreProcessor<IAudited>
{
    public Task Process(IAudited request, CancellationToken cancellationToken)
    {
        trace.Add($"AuditedPre({request.GetType().Name})");
        return Task.CompletedTask;
    }
}

/// <summary>Names Cached directly, which decides whether the interface processor above reaches Cached too.</summary>
public sealed class CachedPre(Trace trace) : IRequestPreProcessor<Cached>
{
    public Task Process(Cached request, CancellationToken cancellationToken)
    {
        trace.Add("CachedPre");
        return Task.CompletedTask;
    }
}
