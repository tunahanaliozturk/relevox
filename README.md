<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/brand/capar-mark-dark.svg">
    <img src="docs/brand/capar-mark.svg" alt="Çapar" width="120">
  </picture>
</p>

# Çapar

A mediator for .NET whose dispatch is written by a source generator at build time, with the same handler
signatures as MediatR 12. MIT licensed, no licence key, no telemetry.

A çapar was a mounted courier who carried orders across the Ottoman roads at a gallop. This one carries
requests to their handlers.

> **Status: feature complete for 1.0, not released yet.** Nothing is on NuGet. To try it, pack the two
> projects into a local feed the way [`eng/package-smoke.sh`](eng/package-smoke.sh) does.

## Why

MediatR 13 and later are licensed under RPL-1.5 or a commercial agreement and check a licence key at run
time. 12.5.0 is the last Apache-2.0 release and is frozen. Çapar is a place to move to without rewriting
anything:

- **Same signatures.** `IRequestHandler<TRequest, TResponse>.Handle(request, cancellationToken)` returning
  `Task<TResponse>`, the same pipeline behaviours, pre and post processors, exception handlers and actions,
  streams and notifications. Migrating changes packages, `using` directives and one registration call.
- **Same behaviour.** Every scenario in the [differential tests](tests/Capar.DifferentialTests) runs on
  MediatR 12.5.0 and on Çapar, and the two must agree on results, handler order and exceptions, down to
  which processors run. [compatibility.md](docs/compatibility.md) lists what they pinned down.
- **Mistakes found by the compiler.** No assembly scanning. A request with no handler, or two, or a handler
  the application cannot call, is reported while building, with a [page per rule](docs/diagnostics.md).
- **Trimming and NativeAOT** without annotations. A sample is published with NativeAOT and run in CI.

## How fast

Measured with BenchmarkDotNet 0.15.8 on an Intel Core Ultra 7 255H, .NET 10.0.13, Windows 11. Full tables,
and what each number does and does not show, are in
[docs/benchmark-results/2026-10-17-m4.md](docs/benchmark-results/2026-10-17-m4.md).

| | MediatR 12.5 | Çapar | Çapar as a share of MediatR |
|---|---:|---:|---:|
| Send, through `ISender` | 37.1 ns, 128 B | 15.7 ns, 24 B | 0.42 |
| Send, through the generated mediator | 37.1 ns, 128 B | 9.3 ns, 24 B | 0.25 |
| Send with three behaviours | 141 ns, 656 B | 66 ns, 392 B | 0.47 |
| Publish to five handlers | 138 ns | 53.6 ns | 0.39 |
| Stream of 100 items | 2.45 µs, 592 B | 1.40 µs, 328 B | 0.57 |
| Register and send once | 93.4 µs | 3.0 µs | 0.03 |
| Eight request types in turn, of 500 | 113.5 ns | 28.2 ns | 0.25 |

Mediator 3.0, the other source-generated library, is faster where it holds handlers as singletons, which it
does by default and Çapar does on request. With transient handlers in both, Çapar's typed send is 9.5 ns
against Mediator's 28.3 ns. Mediator's handlers return `ValueTask`, so moving to it means rewriting them;
that is the trade [ADR 0002](docs/adr/0002-task-not-valuetask.md) explains.

## Quick start

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/images/project-layout-dark.png">
  <img src="docs/images/project-layout-light.png" alt="The API and test projects reference the application, which references Capar and calls AddCapar; libraries below it reference only Capar.Contracts" width="800">
</picture>

The project that calls `AddCapar` references `Capar` and gets the one mediator. Libraries reference only
`Capar.Contracts`, which has no dependencies. Projects above the application use its mediator.

```csharp
using Capar;

builder.Services.AddCapar();

public sealed record GetOrder(int Id) : IRequest<Order>;

public sealed class GetOrderHandler(OrderStore store) : IRequestHandler<GetOrder, Order>
{
    public Task<Order> Handle(GetOrder request, CancellationToken cancellationToken) =>
        store.FindAsync(request.Id, cancellationToken);
}

// Wherever ISender, IPublisher or IMediator is injected:
Order order = await sender.Send(new GetOrder(42), cancellationToken);
```

Behaviours, processors and lifetimes are configured as in MediatR:

```csharp
builder.Services.AddCapar(options =>
{
    options.AddOpenBehavior(typeof(Validation<,>));
    options.AddOpenRequestPreProcessor(typeof(Stamp<>));
    options.HandlerLifetime = ServiceLifetime.Singleton;
});
```

## Moving from MediatR

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/images/migration-dark.png">
  <img src="docs/images/migration-light.png" alt="Swap the packages, run dotnet format analyzers with CPR0007, build. Tried on Clean Architecture and eShop with every test passing" width="880">
</picture>

The command rewrites `using MediatR`, `AddMediatR` and the scanning settings. In an IDE the same fix is
**Move from MediatR to Capar**, applied to the whole solution. Both trials are written up in
[migration-trial.md](docs/migration-trial.md).

[docs/migration.md](docs/migration.md) has the details and the few deliberate differences.
[samples/Migration](samples/Migration) holds the same order service on both libraries; CI runs the two and
fails if their output differs. [samples/Capar.Samples.Web](samples/Capar.Samples.Web) is an ASP.NET Core
minimal API on Çapar, with its [`.http` file](samples/Capar.Samples.Web/Capar.Samples.Web.http) and tests that
drive it over HTTP.

## How it works

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/images/how-it-works-dark.png">
  <img src="docs/images/how-it-works-light.png" alt="At build time the generator reads the handlers and writes CaparMediator, AddCapar and diagnostics. At send time a typed overload or ISender goes to the handler, through behaviours when there are any" width="760">
</picture>

Nothing is scanned at run time. `ISender.Send` costs one frozen-dictionary lookup, however many requests there
are, and a request with no behaviours skips the pipeline entirely.

## Decisions

- [1. Source-generated dispatch](docs/adr/0001-source-generated-dispatch.md)
- [2. Handlers return Task, not ValueTask](docs/adr/0002-task-not-valuetask.md)
- [3. Target net10.0 only](docs/adr/0003-net10-only.md)
- [4. Resolve handlers per call, by concrete type](docs/adr/0004-resolve-per-call.md)
- [5. No `namespace MediatR` shim](docs/adr/0005-no-mediatr-namespace-shim.md)
- [6. Clean-room rule, and MediatR 12.5.0 as the test oracle](docs/adr/0006-clean-room.md)
- [7. `AddCapar` lives in `Microsoft.Extensions.DependencyInjection`](docs/adr/0007-addcapar-namespace.md)
- [8. Generic handlers are closed at build time](docs/adr/0008-generic-handlers.md)
- [9. Generic notification handlers also close at run time](docs/adr/0009-open-notification-handlers-at-run-time.md)

## Building

The .NET 10 SDK is all it needs.

```bash
dotnet build Capar.slnx
dotnet run --project tests/Capar.SourceGenerator.Tests   # generator: output, diagnostics, incrementality
dotnet run --project tests/Capar.DifferentialTests       # every scenario on MediatR 12.5.0 and on Çapar
bash eng/package-smoke.sh                                # pack, install into a new app, run it
dotnet run --project tools/Capar.LicenseAudit -- .       # every dependency permissively licensed
```

The diagrams in this file are drawn by `tools/diagrams.py`; `CHROME=<path to Chrome or Edge> python tools/diagrams.py`
renders them again after a change.

[CONTRIBUTING.md](CONTRIBUTING.md) has the ground rules, including the clean-room rule: no code from
MediatR 13 or later.

## Licence

MIT.
