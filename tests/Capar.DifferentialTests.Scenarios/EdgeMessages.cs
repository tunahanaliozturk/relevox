namespace Scenarios;

// Messages for the edge cases a review of M1 turned up. Each one is here because the first version of the
// generator got it wrong or could have.

public sealed record Order(int Id);

/// <summary>A generic domain event. Only its closed form is ever handled.</summary>
public sealed record EntityCreated<T>(T Item) : INotification;

public sealed class OrderCreatedHandler(Trace trace) : INotificationHandler<EntityCreated<Order>>
{
    public Task Handle(EntityCreated<Order> notification, CancellationToken cancellationToken)
    {
        trace.Add($"OrderCreatedHandler({notification.Item.Id})");
        return Task.CompletedTask;
    }
}

/// <summary>A struct notification. Variance does not apply to value types, so catch-all handlers miss it.</summary>
public readonly record struct Beep(int Level) : INotification;

public sealed class BeepHandler(Trace trace) : INotificationHandler<Beep>
{
    public Task Handle(Beep notification, CancellationToken cancellationToken)
    {
        trace.Add($"BeepHandler({notification.Level})");
        return Task.CompletedTask;
    }
}

public sealed record Fragile : INotification;

public sealed class FragileFirst(Trace trace) : INotificationHandler<Fragile>
{
    public Task Handle(Fragile notification, CancellationToken cancellationToken)
    {
        trace.Add("FragileFirst");
        return Task.CompletedTask;
    }
}

/// <summary>Cannot be constructed, to see whether earlier handlers run before that is discovered.</summary>
public sealed class FragileBroken : INotificationHandler<Fragile>
{
    public FragileBroken() => throw new InvalidOperationException("this handler cannot be built");

    public Task Handle(Fragile notification, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed record Ticker(int Count) : IStreamRequest<int>;

/// <summary>
/// Not an iterator: it builds its stream elsewhere and only records the token it was handed. That is what
/// shows whether a caller's WithCancellation token reaches a handler that never declared
/// [EnumeratorCancellation].
/// </summary>
public sealed class TickerHandler(Trace trace) : IStreamRequestHandler<Ticker, int>
{
    private static int s_instances;
    private readonly int _instance = Interlocked.Increment(ref s_instances);

    public IAsyncEnumerable<int> Handle(Ticker request, CancellationToken cancellationToken)
    {
        trace.Add($"TickerHandler(canBeCanceled={cancellationToken.CanBeCanceled})");
        return Produce(request.Count, _instance);
    }

    private static async IAsyncEnumerable<int> Produce(int count, int instance)
    {
        for (int index = 0; index < count; index++)
        {
            yield return instance;
        }

        await Task.CompletedTask;
    }
}
