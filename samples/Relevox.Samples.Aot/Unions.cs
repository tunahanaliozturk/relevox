// C# 15 unions, compiled for net11.0 only: a union needs .NET 11's System.Runtime.CompilerServices.IUnion. The
// project file turns on C# 15 for that framework alone. See docs/unions.md.

using Relevox;

namespace Relevox.Samples.Aot;

public sealed record Order(int Id);

public sealed record NotFound;

/// <summary>The handler's answer: an order, or that there is none. No result library needed.</summary>
public union FindResult(Order, NotFound);

public sealed record FindOrder(int Id) : IRequest<FindResult>;

public sealed class FindOrderHandler : IRequestHandler<FindOrder, FindResult>
{
    public Task<FindResult> Handle(FindOrder request, CancellationToken cancellationToken) =>
        Task.FromResult<FindResult>(request.Id > 0 ? new Order(request.Id) : new NotFound());
}

/// <summary>What a caller, such as an endpoint, does with the answer. The switch needs no discard arm.</summary>
public static class OrderPage
{
    public static string Show(FindResult result) => result switch
    {
        Order order => $"order {order.Id}",
        NotFound => "not found",
    };
}

public sealed record Placed(int OrderId) : INotification;

public sealed record Cancelled(int OrderId) : INotification;

/// <summary>A union published as one notification. Its handlers run; a handler of a case would not.</summary>
public union OrderEvent(Placed, Cancelled) : INotification;

public sealed class OrderHistory(Journal journal) : INotificationHandler<OrderEvent>
{
    public Task Handle(OrderEvent notification, CancellationToken cancellationToken)
    {
        journal.Write(notification switch
        {
            Placed placed => $"placed {placed.OrderId}",
            Cancelled cancelled => $"cancelled {cancelled.OrderId}",
        });
        return Task.CompletedTask;
    }
}
