namespace Scenarios;

// Generic handlers. MediatR registers a generic notification handler as an open generic by default and the
// container closes it per notification; generic request handlers it closes only with RegisterGenericHandlers.
// The generic handlers are declared before the written ones on purpose: MediatR runs closed-by-the-container
// handlers after the ones its scan registered, whatever the declaration order.

public interface IShipmentEvent : INotification;

public sealed class ShipmentTracker<T>(Trace trace) : INotificationHandler<T>
    where T : IShipmentEvent
{
    public Task Handle(T notification, CancellationToken cancellationToken)
    {
        trace.Add($"tracker {notification}");
        return Task.CompletedTask;
    }
}

public sealed class ShipmentAudit<T>(Trace trace) : INotificationHandler<T>
    where T : class, IShipmentEvent
{
    public Task Handle(T notification, CancellationToken cancellationToken)
    {
        trace.Add($"audit {notification}");
        return Task.CompletedTask;
    }
}

public sealed record Dispatched(string Parcel) : IShipmentEvent;

public sealed record Delivered(string Parcel) : IShipmentEvent;

public readonly record struct Scanned(string Parcel) : IShipmentEvent;

public sealed record Rerouted<T>(T Destination) : IShipmentEvent;

public sealed record Mislaid(string Parcel) : INotification;

public readonly record struct Weighed<T>(T Kilograms) : IShipmentEvent;

// Built from a type parameter, so no declaration names the closed type. Only a closing at run time reaches it,
// as in a generic base entity that raises new EntityCreated<TSelf>().
public static class Shipments
{
    public static INotification Reroute<T>(T destination) => new Rerouted<T>(destination);

    public static INotification Weigh<T>(T kilograms) => new Weighed<T>(kilograms);
}

public sealed class DispatchedLogger(Trace trace) : INotificationHandler<Dispatched>
{
    public Task Handle(Dispatched notification, CancellationToken cancellationToken)
    {
        trace.Add($"logger {notification.Parcel}");
        return Task.CompletedTask;
    }
}

public sealed class ReroutedToCity(Trace trace) : INotificationHandler<Rerouted<string>>
{
    public Task Handle(Rerouted<string> notification, CancellationToken cancellationToken)
    {
        trace.Add($"rerouted to {notification.Destination}");
        return Task.CompletedTask;
    }
}

public sealed class MislaidHandler(Trace trace) : INotificationHandler<Mislaid>
{
    public Task Handle(Mislaid notification, CancellationToken cancellationToken)
    {
        trace.Add($"mislaid {notification.Parcel}");
        return Task.CompletedTask;
    }
}

// Generic requests, closed over the types that satisfy the constraint.

public interface IStockItem;

public sealed class Bolt : IStockItem;

public sealed class Nut : IStockItem;

public sealed class Washer : IStockItem;

public sealed record StockLevel<T>(int Warehouse) : IRequest<string>
    where T : IStockItem;

public sealed class StockLevelHandler<T>(Trace trace) : IRequestHandler<StockLevel<T>, string>
    where T : IStockItem
{
    public Task<string> Handle(StockLevel<T> request, CancellationToken cancellationToken)
    {
        trace.Add($"generic stock level of {typeof(T).Name}");
        return Task.FromResult($"{typeof(T).Name} in {request.Warehouse}");
    }
}

public sealed record Restock<T>(int Amount) : IRequest
    where T : class, IStockItem;

public sealed class RestockHandler<T>(Trace trace) : IRequestHandler<Restock<T>>
    where T : class, IStockItem
{
    public Task Handle(Restock<T> request, CancellationToken cancellationToken)
    {
        trace.Add($"restocked {request.Amount} {typeof(T).Name}");
        return Task.CompletedTask;
    }
}

public sealed class NutStockLevel(Trace trace) : IRequestHandler<StockLevel<Nut>, string>
{
    public Task<string> Handle(StockLevel<Nut> request, CancellationToken cancellationToken)
    {
        trace.Add("written stock level of Nut");
        return Task.FromResult($"nuts in {request.Warehouse}");
    }
}
