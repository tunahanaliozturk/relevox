namespace Scenarios;

// Requests for cases the M2 review raised: where the exception behaviours sit relative to the processor
// behaviours, whether a send stays asynchronous next to a behaviour that is not, and SetHandled(null).

/// <summary>Throws from its handler or from its pre-processor, depending on <see cref="Mode"/>.</summary>
public sealed record Guarded(string Mode) : IRequest<string>;

public sealed class GuardedHandler(Trace trace) : IRequestHandler<Guarded, string>
{
    public Task<string> Handle(Guarded request, CancellationToken cancellationToken)
    {
        trace.Add("GuardedHandler");
        return request.Mode == "handler" ? throw new InvalidOperationException("handler") : Task.FromResult("ok");
    }
}

public sealed class GuardedPre(Trace trace) : IRequestPreProcessor<Guarded>
{
    public Task Process(Guarded request, CancellationToken cancellationToken)
    {
        trace.Add("GuardedPre");
        return request.Mode == "pre" ? throw new InvalidOperationException("pre") : Task.CompletedTask;
    }
}

public sealed class GuardedPost(Trace trace) : IRequestPostProcessor<Guarded, string>
{
    public Task Process(Guarded request, string response, CancellationToken cancellationToken)
    {
        trace.Add($"GuardedPost({response})");
        return Task.CompletedTask;
    }
}

public sealed class GuardedRecovery(Trace trace) : IRequestExceptionHandler<Guarded, string, InvalidOperationException>
{
    public Task Handle(Guarded request, InvalidOperationException exception, RequestExceptionHandlerState<string> state, CancellationToken cancellationToken)
    {
        trace.Add($"GuardedRecovery({exception.Message})");
        state.SetHandled("fallback");
        return Task.CompletedTask;
    }
}

/// <summary>Its handler throws before returning a task.</summary>
public sealed record Brittle : IRequest<int>;

public sealed class BrittleHandler(Trace trace) : IRequestHandler<Brittle, int>
{
    public Task<int> Handle(Brittle request, CancellationToken cancellationToken)
    {
        trace.Add("BrittleHandler");
        throw new NotSupportedException("brittle");
    }
}

/// <summary>Passes straight through without being async, so it cannot turn a throw into a faulted task itself.</summary>
public sealed class BrittlePassThrough(Trace trace) : IPipelineBehavior<Brittle, int>
{
    public Task<int> Handle(Brittle request, RequestHandlerDelegate<int> next, CancellationToken cancellationToken)
    {
        trace.Add("BrittlePassThrough");
        return next(cancellationToken);
    }
}

/// <summary>A request whose response may be null, so an exception handler can "handle" with null.</summary>
public sealed record Nullish : IRequest<string?>;

public sealed class NullishHandler(Trace trace) : IRequestHandler<Nullish, string?>
{
    public Task<string?> Handle(Nullish request, CancellationToken cancellationToken)
    {
        trace.Add("NullishHandler");
        throw new InvalidOperationException("nullish");
    }
}

public sealed class NullishRecovery(Trace trace) : IRequestExceptionHandler<Nullish, string?, Exception>
{
    public Task Handle(Nullish request, Exception exception, RequestExceptionHandlerState<string?> state, CancellationToken cancellationToken)
    {
        trace.Add("NullishRecovery");
        state.SetHandled(null);
        return Task.CompletedTask;
    }
}
