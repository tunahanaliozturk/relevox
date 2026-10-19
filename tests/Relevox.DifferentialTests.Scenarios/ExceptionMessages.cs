namespace Scenarios;

// Exception handlers and actions. Both libraries register these whatever the profile, so they are written
// for requests of their own and change nothing in the other scenarios.

/// <summary>Throws an exception chosen by <see cref="Mode"/>, or returns normally for "ok".</summary>
public sealed record Risky(string Mode) : IRequest<string>;

public sealed class RiskyHandler(Trace trace) : IRequestHandler<Risky, string>
{
    public Task<string> Handle(Risky request, CancellationToken cancellationToken)
    {
        trace.Add($"RiskyHandler({request.Mode})");
        return request.Mode switch
        {
            "invalid" => throw new InvalidOperationException("invalid"),
            "argument" => throw new ArgumentOutOfRangeException(nameof(request), "argument"),
            "unsupported" => throw new NotSupportedException("unsupported"),
            _ => Task.FromResult("fine"),
        };
    }
}

public sealed class RiskyInvalidHandler(Trace trace) : IRequestExceptionHandler<Risky, string, InvalidOperationException>
{
    public Task Handle(Risky request, InvalidOperationException exception, RequestExceptionHandlerState<string> state, CancellationToken cancellationToken)
    {
        trace.Add("RiskyInvalidHandler");
        state.SetHandled("recovered from invalid");
        return Task.CompletedTask;
    }
}

/// <summary>Looks at argument exceptions and handles none of them.</summary>
public sealed class RiskyArgumentObserver(Trace trace) : IRequestExceptionHandler<Risky, string, ArgumentException>
{
    public Task Handle(Risky request, ArgumentException exception, RequestExceptionHandlerState<string> state, CancellationToken cancellationToken)
    {
        trace.Add($"RiskyArgumentObserver({exception.GetType().Name})");
        return Task.CompletedTask;
    }
}

/// <summary>Handles argument exceptions, and only looks at anything else.</summary>
public sealed class RiskyAnyHandler(Trace trace) : IRequestExceptionHandler<Risky, string, Exception>
{
    public Task Handle(Risky request, Exception exception, RequestExceptionHandlerState<string> state, CancellationToken cancellationToken)
    {
        trace.Add($"RiskyAnyHandler({exception.GetType().Name})");
        if (exception is ArgumentException)
        {
            state.SetHandled("recovered from argument");
        }

        return Task.CompletedTask;
    }
}

public sealed class RiskyAction(Trace trace) : IRequestExceptionAction<Risky, Exception>
{
    public Task Execute(Risky request, Exception exception, CancellationToken cancellationToken)
    {
        trace.Add($"RiskyAction({exception.GetType().Name})");
        return Task.CompletedTask;
    }
}

public sealed class RiskyInvalidAction(Trace trace) : IRequestExceptionAction<Risky, InvalidOperationException>
{
    public Task Execute(Risky request, InvalidOperationException exception, CancellationToken cancellationToken)
    {
        trace.Add("RiskyInvalidAction");
        return Task.CompletedTask;
    }
}

/// <summary>A request without a response whose handler always throws.</summary>
public sealed record Fragment : IRequest;

public sealed class FragmentHandler(Trace trace) : IRequestHandler<Fragment>
{
    public Task Handle(Fragment request, CancellationToken cancellationToken)
    {
        trace.Add("FragmentHandler");
        throw new InvalidOperationException("fragment");
    }
}

public sealed class FragmentRecovery(Trace trace) : IRequestExceptionHandler<Fragment, Unit, Exception>
{
    public Task Handle(Fragment request, Exception exception, RequestExceptionHandlerState<Unit> state, CancellationToken cancellationToken)
    {
        trace.Add("FragmentRecovery");
        state.SetHandled(Unit.Value);
        return Task.CompletedTask;
    }
}
