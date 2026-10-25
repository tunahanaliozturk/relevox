# Changelog

All notable changes to this project are written down here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Fixed

- An open exception handler or action constrained to an exception class, such as `where TException :
  DomainException`, now also runs at each class below it that the solution declares, abstract ones included, as
  MediatR's does: a thrown `NotFoundException : DomainException` reaches it at both classes, where 1.4 reached it
  once (#101). Classes declared only in packages are still not looked up.

### Added

- `[PipelineOrder(n)]` on a pipeline or stream behaviour places it by number instead of by registration order:
  lower runs further out, a behaviour without it counts as 0, built-ins included, and equal numbers keep
  registration order, so nothing moves where it is not used (#97, ADR 0019). The order is worked out on the
  first send of each request type; one whose behaviours it moves gets one new array per send. The message map
  lists behaviours in that order and gives each ordered one an `order` field, a compatible schema change.
- RVX0013, Info: two pipeline or stream behaviours that could run for the same request with the same
  `PipelineOrder`, which then run in registration order (#98).
- [docs/compatibility.md](docs/compatibility.md#the-pipeline) shows how an open behaviour with generic
  constraints, on the request or on the response, runs only for the requests they allow, and a differential
  scenario pins both against MediatR (#99).

## [1.4.0] - 2026-10-25

Architecture rules: the analyzers point out handlers that send other requests or each other in a cycle,
pipeline code that blocks on a task, and notifications published from a layer that should not publish them.
Every one is a suggestion until a project raises it in `.editorconfig`, so moving to 1.4.0 adds no warning to
any build; ADR 0018 records why. The pre- and post-processor behaviours also got faster.

### Added

- Architecture rules, Info by default so an upgrade adds no warning to any build, shipped in `Relevox.Contracts`
  so they run where the handlers are (ADR 0018). Raise any of them in `.editorconfig` to enforce it
  ([docs/diagnostics.md](docs/diagnostics.md#architecture-rules)).
- RVX0008: a handler that sends another request or creates a stream, which runs a second pipeline inside the
  first.
- RVX0009: handlers in the project that send each other in a cycle, reported at each send with the way back,
  such as `A -> B -> C -> A`.
- RVX0010: `.Result`, `.Wait()` or `.GetAwaiter().GetResult()` on a task inside a handler, behaviour or
  processor. A task that is plainly finished, such as `Task.FromResult(x)`, is not reported.
- RVX0011: a notification published from a layer its kind may not be published from, with layers and rules in
  `.editorconfig` (`relevox.layer.domain = MyApp.Domain`, `relevox.publish_from.infrastructure =
  MyApp.Domain.Common.BaseEvent`). Nothing is checked until a project defines them.
- RVX0012, a warning: a layer setting RVX0011 cannot use, such as a `publish_from` for a layer no setting defines.
- [docs/diagnostics.md](docs/diagnostics.md#a-handler-that-does-not-pass-its-token-on) recommends raising .NET's
  CA2016 for handlers that do not pass their `CancellationToken` on, instead of a Relevox rule (#50).

### Performance

- `Send(object)` for a request without a response boxes `Unit` once instead of on every send, 24 bytes less.
- The pre- and post-processor behaviours build no state machine while their processors and the rest of the
  pipeline finish synchronously, and return the pipeline's own task instead of a new one: a request whose pre- and
  post-processor run went from 121.9 ns and 552 bytes to 94.1 ns and 408 bytes, in a new `BuiltInBenchmarks` row.
  They still behave as MediatR's `async` behaviours: anything thrown ends up in the returned task, faulted or
  canceled, and changes the handler makes to the execution context, such as an `AsyncLocal<T>`, are put back.

## [1.3.0] - 2026-10-23

The message map, off by default: a project that turns it on gets a JSON and a Markdown file listing what its
mediator dispatches, each pipeline in the order it runs, and where each message is sent from, to commit and
check in CI. [docs/message-map.md](docs/message-map.md) shows how; ADRs 0014 to 0017 record the decisions.

### Added

- The message map switch: `<RelevoxMessageMap>true</RelevoxMessageMap>` in a project writes a JSON map of what
  its mediator dispatches to `obj/<configuration>/<framework>/relevox-message-map.json`, or to
  `RelevoxMessageMapPath` ([docs/message-map.md](docs/message-map.md), ADR 0014). The generator compiles the
  map into the assembly and an MSBuild task in the package copies it out, only when it changed. Off by default;
  with it off, the generated code is unchanged.
- Message map schema version 1 ([docs/message-map.md](docs/message-map.md), ADR 0015): for each request its
  handler, response, behaviours, pre- and post-processors, exception actions and handlers in the order they run,
  and the project of each; streams the same way; each notification with its handlers in order. Behaviours are
  read from the `AddRelevox` options lambda, open ones checked against each request's constraints; registrations
  the build cannot read are listed under `unread`. The JSON schema is
  [docs/message-map.schema.json](docs/message-map.schema.json). Within 1.x fields are only added.
- A Markdown view of the message map next to the JSON, `relevox-message-map.md` (ADR 0016): a table per request
  and stream with its pipeline in order, and a Mermaid diagram per notification with its handlers, both rendered
  by GitHub.
- Call sites in the message map (ADR 0017): each request, stream and notification lists the `Send`,
  `Publish` and `CreateStream` calls in the project that pass it, by file relative to the project and line.
- [docs/message-map.md](docs/message-map.md) shows how to commit the map and fail a GitHub Actions build when it
  is stale; this repository's CI runs that step on the package smoke application. The map was checked by hand
  on eShop ([docs/migration-trial.md](docs/migration-trial.md)).

### Performance

- No change to dispatch: the runtime and the code the generator writes for it are the same as in 1.2.0. Every
  benchmark was run again on this code, on a machine short of memory, so every library's times are higher than
  in the 1.2.0 run and the ratios agree within noise ([results](docs/benchmark-results/2026-10-23-1.3.0.md)).

## [1.2.0] - 2026-10-21

OpenTelemetry tracing and metrics, off by default. The names are fixed in
[ADR 0013](docs/adr/0013-telemetry-names.md) and do not change within 1.x; [docs/telemetry.md](docs/telemetry.md)
shows how to turn them on and what they emit.

### Added

- Spans for `Send`, `Publish` and `CreateStream` on the `Relevox` activity source, turned on with
  `options.Telemetry.Enabled = true` ([docs/telemetry.md](docs/telemetry.md), ADR 0013). Span names and
  attributes come from constants in the generated code. A failure sets the span status to `Error`, adds
  `error.type` and an exception event, and reaches the caller unchanged. A stream gets one span per
  enumeration, and a notification type known only at run time is named from its type. Off by default; with it
  off, each call reads one flag.
- The `relevox.operation.duration` histogram, in seconds, on the `Relevox` meter, under the same switch. Each
  measurement carries `relevox.operation` and `relevox.message.type`, and `error.type` when the operation
  failed, so errors per message type are a query rather than a second instrument.
- `options.Telemetry.NotificationHandlerSpans`, which gives each notification handler a span of its own,
  `handle OrderPlaced`, tagged with `relevox.handler.type`, under the publish span. A failing handler's span is
  marked `Error`. It works with `ForeachAwaitPublisher`, `TaskWhenAllPublisher` and a custom publisher, and for
  notification types known only at run time. Off by default even with telemetry on; with it off, a publish does
  no extra work per handler.
- `RelevoxTelemetry.SourceName`, for `AddSource` and `AddMeter`.
- The web sample exports its spans and metrics over OTLP when `OTEL_EXPORTER_OTLP_ENDPOINT` is set, and its
  README shows how to watch them in the standalone Aspire dashboard.

### Performance

- Telemetry off costs a send one flag read: 1.08 ns for a singleton handler's typed send, against 1.06 ns in the
  send benchmark. On with nothing listening, 2.0 ns; on with a listener, about 230 ns and 576 B, almost all of it
  the `Activity`. Every benchmark was run on this code
  ([results](docs/benchmark-results/2026-10-21-1.2.0.md), [telemetry](docs/benchmark-results/2026-10-21-telemetry.md)).

## [1.1.0] - 2026-10-20

### Added

- `Relevox.Testing`, a package for test projects ([docs/testing.md](docs/testing.md), [ADR 0012](docs/adr/0012-testing-package.md)):
  - `FakeMediator`, an `IMediator` for unit tests. It answers what the test set up with `Setup`, `SetupAsync`,
    `SetupStream` and `Throws`, records what was sent and published, and fails a request nobody set up with its
    type name instead of answering `default`.
  - `AddRelevoxNotificationRecorder` and `NotificationRecorder`, which record every notification the real
    mediator publishes while the application's handlers run as before.

### Changed

- A handler written as a `struct` is reported as RVX0003, an error, instead of being dropped without a word,
  which left only RVX0001 on its request. A project with one fails to build until it is made a class; under
  1.0 such a handler never ran.
- Packing compares each package's public API, `Relevox.Internal` included, with the last release and fails on a
  breaking change, as [ADR 0011](docs/adr/0011-internal-api-is-a-contract.md) promises.

### Fixed

- A generic exception handler or action constrained to one exception class, such as
  `where TException : DomainException`, runs instead of being reported as RVX0004. Only requests that meet its
  request type parameter's constraints look it up, which is new for every generic exception handler: one used
  to put every request on the exception path. [compatibility.md](docs/compatibility.md) states the remaining
  difference from MediatR.
- `IEnumerable<INotificationHandler<T>>` for an abstract or interface notification type holds the same handlers
  as MediatR's, base-type handlers included.
- Generated code compiles with handlers whose nullable annotations differ from their messages': a handler
  compiled with nullable annotations off for a request declared `IRequest<string?>` (CS8631), a generic
  notification handler closed over `Envelope<string?>` (CS8619), and handlers for `Envelope<string>` and
  `Envelope<string?>` side by side (CS8620).
- With `HandlerLifetime = Singleton`, a handler the application registered again as transient after
  `AddRelevox` was still held by the mediator, so one instance was shared where the container hands out a new
  one each time. Which handlers the mediator may hold is now read when the first mediator is built.

Every benchmark was run again on this code ([results](docs/benchmark-results/2026-10-20-1.1.0.md)). Nothing on the
send, publish or stream paths changed; registering allocates 2 KB more.

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
