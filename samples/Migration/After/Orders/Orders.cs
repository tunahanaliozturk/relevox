using Capar;
using Capar.Pipeline;

namespace Shop.Orders;

// The order service from samples/Migration/Before, moved from MediatR 12.5 to Capar. Only the usings at the
// top of this file changed; the project file now references Capar.Contracts instead of MediatR.

public sealed record OrderView(int Id, string Customer, int Quantity, bool Cancelled)
{
    public static OrderView Missing(int id) => new(id, "(none)", 0, Cancelled: false);
}

public sealed class OrderStore
{
    private readonly List<OrderView> _orders = [];

    public int Add(string customer, int quantity)
    {
        int id = _orders.Count + 1;
        _orders.Add(new OrderView(id, customer, quantity, Cancelled: false));
        return id;
    }

    public OrderView? Find(int id) => _orders.Find(order => order.Id == id);

    public void Cancel(int id)
    {
        int index = _orders.FindIndex(order => order.Id == id);
        _orders[index] = _orders[index] with { Cancelled = true };
    }

    public IEnumerable<OrderView> For(string customer) => _orders.Where(order => order.Customer == customer);
}

/// <summary>What happened, in order, so the two versions of the sample can be compared line by line.</summary>
public sealed class Journal
{
    public List<string> Lines { get; } = [];
}

public interface IValidated
{
    string? Problem { get; }
}

public sealed class OrderRejectedException(string problem) : Exception(problem);

public sealed class OrderNotFoundException(int id) : Exception($"Order {id} does not exist.")
{
    public int Id { get; } = id;
}

public sealed record PlaceOrder(string Customer, int Quantity) : IRequest<int>, IValidated
{
    public string? Problem => Quantity > 0 ? null : "The quantity has to be at least one.";
}

public sealed class PlaceOrderHandler(OrderStore store, IPublisher publisher) : IRequestHandler<PlaceOrder, int>
{
    public async Task<int> Handle(PlaceOrder request, CancellationToken cancellationToken)
    {
        int id = store.Add(request.Customer, request.Quantity);
        await publisher.Publish(new OrderPlaced(id, request.Customer), cancellationToken);
        return id;
    }
}

public sealed record GetOrder(int Id) : IRequest<OrderView>;

public sealed class GetOrderHandler(OrderStore store) : IRequestHandler<GetOrder, OrderView>
{
    public Task<OrderView> Handle(GetOrder request, CancellationToken cancellationToken) =>
        Task.FromResult(store.Find(request.Id) ?? throw new OrderNotFoundException(request.Id));
}

public sealed class MissingOrder : IRequestExceptionHandler<GetOrder, OrderView, OrderNotFoundException>
{
    public Task Handle(GetOrder request, OrderNotFoundException exception, RequestExceptionHandlerState<OrderView> state, CancellationToken cancellationToken)
    {
        state.SetHandled(OrderView.Missing(exception.Id));
        return Task.CompletedTask;
    }
}

public sealed record CancelOrder(int Id) : IRequest;

public sealed class CancelOrderHandler(OrderStore store, Journal journal) : IRequestHandler<CancelOrder>
{
    public Task Handle(CancelOrder request, CancellationToken cancellationToken)
    {
        store.Cancel(request.Id);
        journal.Lines.Add($"cancelled order {request.Id}");
        return Task.CompletedTask;
    }
}

public sealed record ListOrders(string Customer) : IStreamRequest<OrderView>;

public sealed class ListOrdersHandler(OrderStore store) : IStreamRequestHandler<ListOrders, OrderView>
{
    public async IAsyncEnumerable<OrderView> Handle(ListOrders request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (OrderView order in store.For(request.Customer))
        {
            await Task.Yield();
            yield return order;
        }
    }
}

public sealed record OrderPlaced(int Id, string Customer) : INotification;

public sealed class SendConfirmation(Journal journal) : INotificationHandler<OrderPlaced>
{
    public Task Handle(OrderPlaced notification, CancellationToken cancellationToken)
    {
        journal.Lines.Add($"confirmation sent to {notification.Customer} for order {notification.Id}");
        return Task.CompletedTask;
    }
}

public sealed class ReserveStock(Journal journal) : INotificationHandler<OrderPlaced>
{
    public Task Handle(OrderPlaced notification, CancellationToken cancellationToken)
    {
        journal.Lines.Add($"stock reserved for order {notification.Id}");
        return Task.CompletedTask;
    }
}

public sealed class Validation<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken) =>
        request is IValidated { Problem: { } problem } ? throw new OrderRejectedException(problem) : next(cancellationToken);
}

public sealed class Stamp<TRequest>(Journal journal) : IRequestPreProcessor<TRequest>
    where TRequest : notnull
{
    public Task Process(TRequest request, CancellationToken cancellationToken)
    {
        journal.Lines.Add($"received {typeof(TRequest).Name}");
        return Task.CompletedTask;
    }
}
