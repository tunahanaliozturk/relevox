# Relevox

[GitHub](https://github.com/tunahanaliozturk/relevox) ·
[Docs](https://github.com/tunahanaliozturk/relevox/tree/main/docs) ·
[Changelog](https://github.com/tunahanaliozturk/relevox/blob/main/CHANGELOG.md) ·
[Moving from MediatR](https://github.com/tunahanaliozturk/relevox/blob/main/docs/migration.md)

A mediator for .NET whose dispatch is written by a source generator at build time, with the same handler
signatures as MediatR 12. MIT licensed, no licence key, no telemetry.

```bash
dotnet add package Relevox             # the application, which calls AddRelevox
dotnet add package Relevox.Contracts   # libraries that only declare requests and handlers
dotnet add package Relevox.Testing     # test projects: a fake mediator and a notification recorder
dotnet add package Relevox.FluentValidation   # a validation behaviour, only for requests with a validator
```

![Send runs the pipeline of behaviours and processors, then the one handler found at build time, and returns the response. Publish hands the notification to the publisher, which runs every handler in order. CreateStream runs the stream behaviours around the one stream handler and returns an IAsyncEnumerable.](https://raw.githubusercontent.com/tunahanaliozturk/relevox/v1.5.1/docs/images/flows-light.png)

- **Same signatures as MediatR 12.** Requests, notifications, streams, pipeline behaviours, pre and post
  processors, exception handlers and actions. Moving over changes packages, `using` directives and the
  registration call.
- **Dispatch decided at build time.** No assembly scanning. A request with no handler, or with two, is
  reported by the compiler.
- **NativeAOT and trimming** without annotations.
- **Faster than MediatR 12.5:** a send takes under half the time through `ISender` and a quarter through
  the generated mediator; startup takes a twenty-fifth. Every number is measured and
  [published with the hardware named](https://github.com/tunahanaliozturk/relevox/tree/main/docs/benchmark-results).

## Packages

| Package | Reference it from |
|---|---|
| `Relevox` | The application, the project that calls `AddRelevox`. It brings the source generator. |
| `Relevox.Contracts` | Projects that only declare requests and handlers: domain, application, feature libraries. |
| `Relevox.Testing` | Test projects: a fake `IMediator` and a recorder for published notifications ([testing.md](https://github.com/tunahanaliozturk/relevox/blob/main/docs/testing.md)). |
| `Relevox.FluentValidation` | The application, for FluentValidation validators: the build gives the behaviour only to the requests that have one ([validation.md](https://github.com/tunahanaliozturk/relevox/blob/main/docs/validation.md)). |

## Quick start

```csharp
using Relevox;

builder.Services.AddRelevox();

public sealed record GetOrder(int Id) : IRequest<Order>;

public sealed class GetOrderHandler(OrderStore store) : IRequestHandler<GetOrder, Order>
{
    public Task<Order> Handle(GetOrder request, CancellationToken cancellationToken) =>
        store.FindAsync(request.Id, cancellationToken);
}

// Anywhere ISender, IPublisher or IMediator is injected:
Order order = await sender.Send(new GetOrder(42), cancellationToken);
```

Handlers are found in the application and in every referenced project that uses `Relevox.Contracts`.

## From MediatR 12

1. Replace the `MediatR` package with `Relevox` in the application and with `Relevox.Contracts` elsewhere.
2. Replace `using MediatR;` with `using Relevox;` and `using MediatR.Pipeline;` with `using Relevox.Pipeline;`.
3. Replace `AddMediatR(cfg => cfg.RegisterServicesFromAssembly(...))` with `AddRelevox()`. Behaviours,
   processors and lifetimes are configured with the same members as before.

In an IDE, steps 2 and 3 are a code fix: after the package swap, apply **Move from MediatR to Relevox** to the
whole solution. From the command line it is `dotnet format analyzers --diagnostics RVX0007 --severity info`.

- [Migration guide](https://github.com/tunahanaliozturk/relevox/blob/main/docs/migration.md)
- [What matches MediatR, and what differs on purpose](https://github.com/tunahanaliozturk/relevox/blob/main/docs/compatibility.md)
- [Diagnostics](https://github.com/tunahanaliozturk/relevox/blob/main/docs/diagnostics.md)
- [Source, issues and samples](https://github.com/tunahanaliozturk/relevox)
