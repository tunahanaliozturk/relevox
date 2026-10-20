namespace Relevox.Testing.Tests;

public sealed record GetPrice(string Sku) : IRequest<decimal>;

public sealed class GetPriceHandler : IRequestHandler<GetPrice, decimal>
{
    public Task<decimal> Handle(GetPrice request, CancellationToken cancellationToken) => Task.FromResult(9.5m);
}

public sealed record Archive(string Sku) : IRequest;

public sealed class ArchiveHandler : IRequestHandler<Archive>
{
    public Task Handle(Archive request, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed record ListSkus(int Count) : IStreamRequest<string>;

public sealed class ListSkusHandler : IStreamRequestHandler<ListSkus, string>
{
    public async IAsyncEnumerable<string> Handle(ListSkus request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        yield return "a";
    }
}

public sealed record PriceChanged(string Sku, decimal Price) : INotification;

/// <summary>Published by nothing but tests; no handler listens.</summary>
public sealed record Unheard(string Sku) : INotification;

public sealed class Ledger
{
    public List<string> Lines { get; } = [];
}

public sealed class PriceLog(Ledger ledger) : INotificationHandler<PriceChanged>
{
    public Task Handle(PriceChanged notification, CancellationToken cancellationToken)
    {
        ledger.Lines.Add($"{notification.Sku} {notification.Price}");
        return Task.CompletedTask;
    }
}
