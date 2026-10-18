# Çapar

A mediator for .NET whose dispatch is written by a source generator at build time, with the same handler
signatures as MediatR 12. MIT licensed, no licence key, no telemetry.

- **Same signatures as MediatR 12.** Requests, notifications, streams, pipeline behaviours, pre and post
  processors, exception handlers and actions. Moving over is a package and namespace change.
- **Dispatch decided at build time.** No assembly scanning. A request with no handler, or with two, is
  reported by the compiler.
- **NativeAOT and trimming** without annotations.
- **Faster than MediatR 12.5:** a send takes under half the time through `ISender` and a quarter through
  the generated mediator; startup takes a thirtieth. Every number is measured and published with the
  hardware named, in the repository.

## Packages

| Package | Reference it from |
|---|---|
| `Capar` | The application, the project that calls `AddCapar`. It brings the source generator. |
| `Capar.Contracts` | Projects that only declare requests and handlers: domain, application, feature libraries. |

## Quick start

```csharp
using Capar;

builder.Services.AddCapar();

public sealed record GetOrder(int Id) : IRequest<Order>;

public sealed class GetOrderHandler(OrderStore store) : IRequestHandler<GetOrder, Order>
{
    public Task<Order> Handle(GetOrder request, CancellationToken cancellationToken) =>
        store.FindAsync(request.Id, cancellationToken);
}

// Anywhere ISender, IPublisher or IMediator is injected:
Order order = await sender.Send(new GetOrder(42), cancellationToken);
```

Handlers are found in the application and in every referenced project that uses `Capar.Contracts`.

## From MediatR 12

1. Replace the `MediatR` package with `Capar` in the application and with `Capar.Contracts` elsewhere.
2. Replace `using MediatR;` with `using Capar;` and `using MediatR.Pipeline;` with `using Capar.Pipeline;`.
3. Replace `AddMediatR(cfg => cfg.RegisterServicesFromAssembly(...))` with `AddCapar()`. Behaviours,
   processors and lifetimes are configured with the same members as before.

The migration guide in the repository covers the rest, including the few places where behaviour differs.
