using Relevox;
using Relevox.Pipeline;

namespace Benchmarks.RelevoxPeer;

// The MediatR scenarios, unchanged apart from the using directive: Relevox keeps MediatR's handler signatures,
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
        Sanity.Count("Relevox behaviour");
        return next(cancellationToken);
    }
}

// The same three behaviours with PipelineOrder, for registering in that order and in reverse.

[PipelineOrder(1)]
public sealed class OrderedFirst : IPipelineBehavior<Piped, int>
{
    public Task<int> Handle(Piped request, RequestHandlerDelegate<int> next, CancellationToken cancellationToken) =>
        next(cancellationToken);
}

[PipelineOrder(2)]
public sealed class OrderedSecond : IPipelineBehavior<Piped, int>
{
    public Task<int> Handle(Piped request, RequestHandlerDelegate<int> next, CancellationToken cancellationToken) =>
        next(cancellationToken);
}

[PipelineOrder(3)]
public sealed class OrderedThird : IPipelineBehavior<Piped, int>
{
    public Task<int> Handle(Piped request, RequestHandlerDelegate<int> next, CancellationToken cancellationToken)
    {
        Sanity.Count("Relevox ordered behaviour");
        return next(cancellationToken);
    }
}

// A request with a FluentValidation validator that passes, for ValidationBenchmarks.

public sealed record Checked(int Value) : IRequest<int>;

public sealed class CheckedHandler : IRequestHandler<Checked, int>
{
    private static readonly Task<int> Answer = Task.FromResult(42);

    public Task<int> Handle(Checked request, CancellationToken cancellationToken)
    {
        Sanity.Count("Relevox validated handler");
        return Answer;
    }
}

public sealed class CheckedValidator : FluentValidation.AbstractValidator<Checked>
{
    public CheckedValidator() => FluentValidation.DefaultValidatorExtensions.GreaterThan(RuleFor(static request => request.Value), 0);
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
        Sanity.Count("Relevox fifth listener");
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

// Processors for Ping only. Configured in the built-ins container, they make each library add its processor
// behaviours for every request, including Piped, which has no processor of its own.

public sealed class PingPre : IRequestPreProcessor<Ping>
{
    public Task Process(Ping request, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class PingPost : IRequestPostProcessor<Ping, int>
{
    public Task Process(Ping request, int response, CancellationToken cancellationToken) => Task.CompletedTask;
}
