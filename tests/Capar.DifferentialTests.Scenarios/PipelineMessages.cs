namespace Scenarios;

// Requests and behaviours for the pipeline scenarios. They are registered only under Profile.Pipeline, so
// the earlier scenarios keep running without any behaviour in the way.

/// <summary>Marks requests the constrained open behaviour applies to.</summary>
public interface IAudited;

public sealed record Cached(string Key) : IRequest<string>, IAudited;

public sealed class CachedHandler(Trace trace) : IRequestHandler<Cached, string>
{
    public Task<string> Handle(Cached request, CancellationToken cancellationToken)
    {
        trace.Add("CachedHandler");
        return Task.FromResult("fresh");
    }
}

public sealed record Flaky : IRequest<int>;

/// <summary>Fails on its first call in a scenario and succeeds on the second.</summary>
public sealed class FlakyHandler(Trace trace) : IRequestHandler<Flaky, int>
{
    public Task<int> Handle(Flaky request, CancellationToken cancellationToken)
    {
        bool first = !trace.Events.Contains("FlakyHandler");
        trace.Add("FlakyHandler");
        return first ? throw new TimeoutException("first attempt") : Task.FromResult(2);
    }
}

public sealed record Retoken : IRequest<bool>;

public sealed class RetokenHandler(Trace trace) : IRequestHandler<Retoken, bool>
{
    public Task<bool> Handle(Retoken request, CancellationToken cancellationToken)
    {
        trace.Add($"RetokenHandler(canBeCanceled={cancellationToken.CanBeCanceled})");
        return Task.FromResult(cancellationToken.CanBeCanceled);
    }
}

/// <summary>Registered first, so it should run outermost.</summary>
public sealed class OuterBehavior<TRequest, TResponse>(Trace trace) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        trace.Add($"Outer>{typeof(TRequest).Name}/{typeof(TResponse).Name}");
        TResponse response = await next();
        trace.Add("Outer<");
        return response;
    }
}

public sealed class InnerBehavior<TRequest, TResponse>(Trace trace) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        trace.Add("Inner>");
        TResponse response = await next();
        trace.Add("Inner<");
        return response;
    }
}

/// <summary>Registered straight into the container after the library's own registration call.</summary>
public sealed class EarlyBehavior<TRequest, TResponse>(Trace trace) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        trace.Add("Early>");
        TResponse response = await next();
        trace.Add("Early<");
        return response;
    }
}

public sealed class LateBehavior<TRequest, TResponse>(Trace trace) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        trace.Add("Late>");
        TResponse response = await next();
        trace.Add("Late<");
        return response;
    }
}

/// <summary>Open, but constrained: the container should only close it over requests that are IAudited.</summary>
public sealed class AuditedBehavior<TRequest, TResponse>(Trace trace) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IAudited
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        trace.Add("Audited>");
        return await next();
    }
}

public sealed class PingAudit(Trace trace) : IPipelineBehavior<Ping, string>
{
    public async Task<string> Handle(Ping request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
    {
        trace.Add("PingAudit>");
        return await next() + "!";
    }
}

/// <summary>Answers without calling the rest of the pipeline.</summary>
public sealed class CacheBehavior(Trace trace) : IPipelineBehavior<Cached, string>
{
    public Task<string> Handle(Cached request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
    {
        trace.Add("CacheHit");
        return Task.FromResult("cached");
    }
}

/// <summary>Calls the rest of the pipeline a second time when the first attempt fails.</summary>
public sealed class RetryBehavior(Trace trace) : IPipelineBehavior<Flaky, int>
{
    public async Task<int> Handle(Flaky request, RequestHandlerDelegate<int> next, CancellationToken cancellationToken)
    {
        try
        {
            return await next();
        }
        catch (TimeoutException)
        {
            trace.Add("Retry");
            return await next();
        }
    }
}

/// <summary>Passes a token of its own to the rest of the pipeline.</summary>
public sealed class RetokenBehavior(Trace trace) : IPipelineBehavior<Retoken, bool>
{
    public async Task<bool> Handle(Retoken request, RequestHandlerDelegate<bool> next, CancellationToken cancellationToken)
    {
        using CancellationTokenSource source = new();
        trace.Add("Retoken");
        return await next(source.Token);
    }
}

/// <summary>A behaviour for a request that returns nothing, which MediatR 12 models as returning Unit.</summary>
public sealed class RingAudit(Trace trace) : IPipelineBehavior<Ring, Unit>
{
    public async Task<Unit> Handle(Ring request, RequestHandlerDelegate<Unit> next, CancellationToken cancellationToken)
    {
        trace.Add("RingAudit>");
        Unit result = await next();
        trace.Add($"RingAudit<{result}");
        return result;
    }
}
