# Changelog

All notable changes to this project are written down here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Changed

- Packing compares each package's public API, `Relevox.Internal` included, with the last release and fails on a
  breaking change, as [ADR 0011](docs/adr/0011-internal-api-is-a-contract.md) promises.

## [1.0.1] - 2026-10-20

### Fixed

- A request with no behaviours is sent straight to its handler again. Since the built-in behaviours became
  slots, before 1.0.0, such a request was remembered as having a pipeline after its first send, so every send
  asked the container for an empty list of behaviours: about 10 ns more per send, and a singleton handler's
  typed send went from under 1 ns to about 11 ns. The README showed numbers measured before that change.
  Every benchmark has been run again ([results](docs/benchmark-results/2026-10-20-1.0.1.md)), and the README
  shows the new numbers.
- `AddRelevox` registers every handler under its interface as well as its class, as MediatR does. Code that took
  a handler as `IRequestHandler<TRequest, TResponse>`, `IStreamRequestHandler<TRequest, TResponse>` or
  `IEnumerable<INotificationHandler<TNotification>>` compiled after moving from MediatR and then failed when the
  container was built or the class was first resolved. A real migration found it: one query handler called
  another through its interface. The interface registrations hand out the class registration, with its
  lifetime, so they return the instance the mediator uses; one the application made under the interface itself
  is kept. [compatibility.md](docs/compatibility.md) lists the two cases where the list of notification handlers
  is still shorter than MediatR's. Registering costs about 0.5 µs more for the benchmark's handlers; sending is
  unchanged.
- A keyed registration the application made before `AddRelevox` no longer stops the unkeyed handler being
  registered.

## [1.0.0] - 2026-10-19

The first release. Relevox is a mediator for .NET 10 whose dispatch is written by a source generator while the
project builds, with the same handler signatures as MediatR 12. Moving over changes packages, `using` directives
and one registration call; the [migration guide](docs/migration.md) covers it, and
[compatibility.md](docs/compatibility.md) lists what matches MediatR and what differs on purpose.

### Packages

- `Relevox`, for the application that calls `AddRelevox`. It brings the source generator.
- `Relevox.Contracts`, for every other project. It has no dependencies, and brings the analyzer and code fix that
  move code from MediatR.

### Added

- Requests, notifications and streams with MediatR 12's names and signatures: `IRequest`, `IRequestHandler`,
  `INotificationHandler` and its synchronous base `NotificationHandler`, `IStreamRequest`, `IStreamRequestHandler`,
  `ISender`, `IPublisher`, `IMediator` and `Unit`. A test checks every public type of MediatR 12.5 for a
  counterpart.
- Pipeline and stream behaviours, pre and post processors, exception handlers and exception actions, registered
  and ordered by MediatR 12's rules. `ForeachAwaitPublisher`, `TaskWhenAllPublisher` and custom
  `INotificationPublisher`s.
- A generated `RelevoxMediator` with a typed `Send`, `Publish` or `CreateStream` overload for each message, which
  calls the handler with nothing to look up. `ISender` and `IPublisher` find the message's runtime type with one
  dictionary lookup.
- `AddRelevox`, which registers every handler by its concrete type with no assembly scanning. `RelevoxOptions`
  has each MediatR configuration member that still means something without scanning, plus `HandlerLifetime`,
  and sits in `Microsoft.Extensions.DependencyInjection` as MediatR's configuration does. An `IMediator` the
  application registered first, such as a test double, is kept.
- Generic handlers, closed while the project builds over the types that satisfy their constraints. A generic
  notification handler is also closed at run time over a notification type the build never saw
  ([ADR 0009](docs/adr/0009-open-notification-handlers-at-run-time.md)).
- NativeAOT and trimming without annotations, checked by a sample published with NativeAOT in CI.
- Stack traces without the mediator's own frames between your handlers and behaviours, and a debugger that steps
  through dispatch into the handler.
- Diagnostics, each with a [help page](docs/diagnostics.md):
  - `RVX0001`: a request has no handler.
  - `RVX0002`: a request has more than one handler.
  - `RVX0003`: a handler that generated code cannot call.
  - `RVX0004`: a generic handler that cannot be registered, and why.
  - `RVX0005`: a referenced project has its own mediator.
  - `RVX0006`: an `AddRelevox` call that would register another project's mediator.
  - `RVX0007`: MediatR code left after the package swap, with the code fix **Move from MediatR to Relevox**. It
    runs as one "Fix all in solution" in an IDE, or as
    `dotnet format analyzers --diagnostics RVX0007 --severity info`.

### Performance

Measured against MediatR 12.5.0 and published with the hardware named in
[docs/benchmark-results](docs/benchmark-results/2026-10-17-m4.md). A send through the generated mediator takes about a quarter of
MediatR's time, through `ISender` under half, and registering and sending once about a thirtieth.

### Tried on real projects

Jason Taylor's Clean Architecture template on MediatR 14.2.0 and Microsoft's eShop on MediatR 13.1.0 were moved
over with the package swap and the one command, every test passing before and after
([migration-trial.md](docs/migration-trial.md)).
