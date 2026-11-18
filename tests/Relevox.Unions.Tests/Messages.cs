using System.Runtime.CompilerServices;
using FluentValidation;

namespace Relevox.Unions.Tests;

/// <summary>What ran, in order, shared by the handlers and behaviours of one container.</summary>
public sealed class Trace
{
    private readonly List<string> _events = [];

    public IReadOnlyList<string> Events => _events;

    public void Add(string entry) => _events.Add(entry);
}

public sealed record Order(int Id);

public sealed record NotFound;

/// <summary>A union as a response: what a result library such as OneOf would otherwise give.</summary>
public union FindResult(Order, NotFound);

public sealed record FindOrder(int Id) : IRequest<FindResult>;

public sealed class FindOrderHandler : IRequestHandler<FindOrder, FindResult>
{
    public Task<FindResult> Handle(FindOrder request, CancellationToken cancellationToken) =>
        Task.FromResult<FindResult>(request.Id > 0 ? new Order(request.Id) : new NotFound());
}

/// <summary>A behaviour closed over the union response, which switches on what the handler returned.</summary>
public sealed class FindOrderAudit(Trace trace) : IPipelineBehavior<FindOrder, FindResult>
{
    public async Task<FindResult> Handle(FindOrder request, RequestHandlerDelegate<FindResult> next, CancellationToken cancellationToken)
    {
        FindResult result = await next(cancellationToken);
        trace.Add(result switch
        {
            Order order => $"audit: order {order.Id}",
            NotFound => "audit: not found",
        });
        return result;
    }
}

public sealed record ListOrders(int Count) : IStreamRequest<FindResult>;

public sealed class ListOrdersHandler : IStreamRequestHandler<ListOrders, FindResult>
{
    public async IAsyncEnumerable<FindResult> Handle(ListOrders request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (int id = 1; id <= request.Count; id++)
        {
            await Task.Yield();
            yield return new Order(id);
        }

        yield return new NotFound();
    }
}

public sealed record Placed(int Id) : INotification;

public sealed record Cancelled(int Id) : INotification;

/// <summary>A union that is itself a notification, over two cases that are notifications too.</summary>
public union OrderEvent(Placed, Cancelled) : INotification;

public sealed class OnOrderEvent(Trace trace) : INotificationHandler<OrderEvent>
{
    public Task Handle(OrderEvent notification, CancellationToken cancellationToken)
    {
        trace.Add(notification switch
        {
            Placed placed => $"event: placed {placed.Id}",
            Cancelled cancelled => $"event: cancelled {cancelled.Id}",
        });
        return Task.CompletedTask;
    }
}

/// <summary>A handler for one of the union's cases, to show which handlers a published union reaches.</summary>
public sealed class OnCancelled(Trace trace) : INotificationHandler<Cancelled>
{
    public Task Handle(Cancelled notification, CancellationToken cancellationToken)
    {
        trace.Add($"cancelled {notification.Id}");
        return Task.CompletedTask;
    }
}

public sealed record Open(int Id);

public sealed record Close(int Id);

/// <summary>A union that is itself a request.</summary>
public union Command(Open, Close) : IRequest<string>;

public sealed class CommandHandler : IRequestHandler<Command, string>
{
    public Task<string> Handle(Command request, CancellationToken cancellationToken) =>
        Task.FromResult(request switch
        {
            Open open => $"opened {open.Id}",
            Close close => $"closed {close.Id}",
        });
}

/// <summary>Makes the build close Relevox.FluentValidation's behaviour over the union request.</summary>
public sealed class CommandValidator : AbstractValidator<Command>
{
    public CommandValidator() =>
        RuleFor(command => command.Value)
            .Must(value => value is not Close { Id: < 1 })
            .WithMessage("Only an existing order can be closed.");
}

/// <summary>A generic union, closed in the response of a request.</summary>
public union Lookup<T>(T, NotFound);

public sealed record FindName(int Id) : IRequest<Lookup<string>>;

public sealed class FindNameHandler : IRequestHandler<FindName, Lookup<string>>
{
    public Task<Lookup<string>> Handle(FindName request, CancellationToken cancellationToken) =>
        Task.FromResult<Lookup<string>>(request.Id == 1 ? "Ada" : new NotFound());
}
