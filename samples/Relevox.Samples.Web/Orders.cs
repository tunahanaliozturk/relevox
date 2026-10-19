using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Relevox;

namespace Shop;

public sealed record OrderView(int Id, string Customer, string Product, int Quantity, bool Cancelled);

public sealed record PlaceOrderBody(string Product, int Quantity);

/// <summary>Orders in memory; a real application would have a database here.</summary>
public sealed class OrderStore
{
    private readonly ConcurrentDictionary<int, OrderView> _orders = new();
    private int _last;

    public OrderView Add(string customer, string product, int quantity)
    {
        int id = Interlocked.Increment(ref _last);
        return _orders[id] = new OrderView(id, customer, product, quantity, Cancelled: false);
    }

    public OrderView? Find(int id) => _orders.GetValueOrDefault(id);

    public void Update(OrderView order) => _orders[order.Id] = order;

    public IEnumerable<OrderView> For(string customer) => _orders.Values.Where(order => order.Customer == customer).OrderBy(order => order.Id);
}

/// <summary>What happened, for GET /activity, so the tests can see notifications and processors run.</summary>
public sealed class Activity
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IEnumerable<string> Lines => _lines;

    public void Add(string line) => _lines.Enqueue(line);
}

/// <summary>
/// The customer making the request, read from a header. Scoped, so every handler and behaviour in one HTTP
/// request sees the same one, as they would a DbContext.
/// </summary>
public sealed class CurrentCustomer(IHttpContextAccessor accessor)
{
    public string Name { get; } = accessor.HttpContext?.Request.Headers["X-Customer"].FirstOrDefault() ?? "guest";
}

public sealed class OrderNotFoundException(int id) : Exception($"Order {id} does not exist.");

public sealed record PlaceOrder(string Product, int Quantity) : IRequest<int>, IValidated
{
    public IEnumerable<(string Field, string Problem)> Problems()
    {
        if (string.IsNullOrWhiteSpace(Product))
        {
            yield return (nameof(Product), "A product is required.");
        }

        if (Quantity is < 1 or > 100)
        {
            yield return (nameof(Quantity), "Between 1 and 100.");
        }
    }
}

public sealed class PlaceOrderHandler(OrderStore store, CurrentCustomer customer) : IRequestHandler<PlaceOrder, int>
{
    public Task<int> Handle(PlaceOrder request, CancellationToken cancellationToken) =>
        Task.FromResult(store.Add(customer.Name, request.Product, request.Quantity).Id);
}

public sealed record GetOrder(int Id) : IRequest<OrderView>;

public sealed class GetOrderHandler(OrderStore store) : IRequestHandler<GetOrder, OrderView>
{
    public Task<OrderView> Handle(GetOrder request, CancellationToken cancellationToken) =>
        Task.FromResult(store.Find(request.Id) ?? throw new OrderNotFoundException(request.Id));
}

public sealed record CancelOrder(int Id) : IRequest;

public sealed class CancelOrderHandler(OrderStore store, IPublisher publisher) : IRequestHandler<CancelOrder>
{
    public async Task Handle(CancelOrder request, CancellationToken cancellationToken)
    {
        OrderView order = store.Find(request.Id) ?? throw new OrderNotFoundException(request.Id);
        store.Update(order with { Cancelled = true });
        await publisher.Publish(new OrderCancelled(order.Id, order.Customer), cancellationToken);
    }
}

public sealed record ListMyOrders : IStreamRequest<OrderView>;

public sealed class ListMyOrdersHandler(OrderStore store, CurrentCustomer customer) : IStreamRequestHandler<ListMyOrders, OrderView>
{
    public async IAsyncEnumerable<OrderView> Handle(ListMyOrders request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (OrderView order in store.For(customer.Name))
        {
            await Task.Yield();
            yield return order;
        }
    }
}

public sealed record OrderCancelled(int Id, string Customer) : INotification;

public sealed class RefundOnCancel(Activity activity) : INotificationHandler<OrderCancelled>
{
    public Task Handle(OrderCancelled notification, CancellationToken cancellationToken)
    {
        activity.Add($"refund issued for order {notification.Id}");
        return Task.CompletedTask;
    }
}

public sealed class EmailOnCancel(Activity activity) : INotificationHandler<OrderCancelled>
{
    public Task Handle(OrderCancelled notification, CancellationToken cancellationToken)
    {
        activity.Add($"email sent to {notification.Customer}");
        return Task.CompletedTask;
    }
}
