<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/brand/capar-mark-dark.svg">
    <img src="docs/brand/capar-mark.svg" alt="Çapar" width="120">
  </picture>
</p>

# Çapar

A mediator for .NET whose dispatch is written by a source generator at build time, with the same handler
signatures as MediatR 12. MIT licensed, no key, no telemetry.

A çapar was a mounted courier who carried orders across the Ottoman roads at a gallop. This one carries
requests to their handlers.

> **Status: in development, not yet usable.** The plan is in [docs/plan.md](docs/plan.md). Nothing is on
> NuGet yet.

## Why

MediatR 13 and later are licensed under RPL-1.5 or a commercial agreement and check a licence key at run
time. 12.5.0 is the last Apache-2.0 release and is frozen. Çapar aims to be the place to move to:

- **Same signatures.** `IRequestHandler<TRequest, TResponse>.Handle(request, cancellationToken)` returning
  `Task<TResponse>`, the same pipeline behaviours, pre and post processors, exception handlers, streams and
  notifications. Migrating is a package and namespace change, not a rewrite.
- **Dispatch decided at build time.** No assembly scanning, no reflection on the send path, missing and
  duplicate handlers reported by the compiler.
- **Trimming and NativeAOT** without annotations.
- **Measured, not asserted.** Every performance claim in this README will point at a committed
  BenchmarkDotNet result, with the hardware named.

## Decisions

- [1. Source-generated dispatch](docs/adr/0001-source-generated-dispatch.md)
- [2. Handlers return Task, not ValueTask](docs/adr/0002-task-not-valuetask.md)
- [3. Target net10.0 only](docs/adr/0003-net10-only.md)
- [4. Resolve handlers per call, by concrete type](docs/adr/0004-resolve-per-call.md)
- [5. No `namespace MediatR` shim](docs/adr/0005-no-mediatr-namespace-shim.md)
- [6. Clean-room rule, and MediatR 12.5.0 as the test oracle](docs/adr/0006-clean-room.md)

## Licence

MIT.
