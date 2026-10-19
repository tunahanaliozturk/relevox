# Changelog

All notable changes to this project are written down here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- Release infrastructure, not used yet: a workflow that runs on a version tag, tests, packs, validates SourceLink
  and determinism, creates the GitHub release from this file and waits for an approval before publishing to
  NuGet with trusted publishing; RELEASING.md, Dependabot, CodeQL for when the repository is public, issue
  and pull request templates and a code of conduct.

- Repository scaffold, quality gates and CI.
- Baseline benchmarks of MediatR 12.5.0 and Mediator 3.0.2: send, pipeline, publish, stream and
  container startup, with a sanity mode that proves every scenario reaches its handler.
- Licence audit tool that fails the build on any non-permissive dependency.
- `Capar.Contracts`: requests, notifications, streams, handlers, behaviours, `ISender`, `IPublisher`,
  `IMediator` and `Unit`, with MediatR 12 names and signatures.
- Source generator: finds handlers in the project and in the projects it references, and emits a mediator
  with a typed overload per message, constant-size dispatch for the interface methods, and an `AddCapar`
  that registers everything by concrete type with no scanning.
- `CaparOptions.Lifetime` for the mediator and `HandlerLifetime` for handlers; scoped and singleton handlers
  are resolved once per mediator and kept.
- Diagnostics CPR0001 to CPR0005.
- Differential tests that run the same scenario source against MediatR 12.5.0 and Çapar, and generator
  tests for diagnostics, determinism, incrementality and strict builds.
- M1 benchmark results.
- Pipeline behaviours, resolved from the container on every send and skipped entirely for a request that
  has none; `AddBehavior` and `AddOpenBehavior` on the options, as in MediatR.
- Pre and post processors with MediatR 12's registration rules, including `AutoRegisterRequestProcessors`.
- Exception handlers and actions, both strategies, without closing generic types at run time.
- Stream pipeline behaviours.
- `INotificationPublisher`, `ForeachAwaitPublisher` and `TaskWhenAllPublisher`.
- Handlers, processors and exception handlers run in declaration order, as MediatR's scan registers them.
- Diagnostics CPR0001 to CPR0005 are reported by an analyzer, in the user's source, so `#pragma warning
  disable` reaches them and the IDE underlines the declaration.
- A NativeAOT sample, published and run in CI, that checks every kind of dispatch.
- Streams forward each item to the handler's enumerator directly: 0.57 of MediatR's time, from 1.12.
- Pipeline steps bound to the chain itself: three behaviours at 0.47 of MediatR's time, from 0.61.
- The reference scan is cached per referenced assembly.
- A 500-request-type benchmark, with Mediator built with transient handlers for a like-for-like comparison.
- Built-in exception and processor behaviours are registered as slots and created only for the requests they
  have work for: a send with three behaviours, in an application with processors, went from 122 ns and 592 B
  to 86 ns and 456 B. Resolving `IEnumerable<IPipelineBehavior<,>>` from the container yourself now returns
  these slots, which throw if run; send through `ISender` or `IMediator`.
- `eng/package-smoke.sh` and a CI job that pack both packages, install them into a new application, run it,
  and check that the packaged analyzer reports CPR0001.
- Package icon and a package README.
- A help page per diagnostic, linked from each rule.
- The generated mediator and `Capar.Internal` are hidden from stack traces, and the debugger steps through them
  into the handler. Where the dispatch code is an async method, `Send(object)`, the default publisher and a
  request with no response that has behaviours, one frame of it is left: .NET does not hide an async state machine. An argument error
  from `Send(object)` now shows the caller as the top frame.
- The generator's cost per edit in a large project with no generic handler: 2,500 handlers went from about
  300 ms to about 100 ms, by not walking every type's interfaces when there is no generic handler and not
  building every member to find nested types ([measured](docs/benchmark-results/2026-10-18-ide.md)). A project
  with a generic handler still walks the interfaces.
- A web sample: an ASP.NET Core minimal API with scoped services in handlers, a validation behaviour, a
  pre-processor, problem details, a notification with two listeners and a streamed list, tested over HTTP in CI.
- Migrations of Jason Taylor's Clean Architecture template from MediatR 14.2.0 and of Microsoft's eShop from
  MediatR 13.1.0, with their tests passing before and after: [docs/migration-trial.md](docs/migration-trial.md).
- CPR0007's fix no longer leaves a blank line at the top of a registration whose first statement it removed.
- A project that has no handlers or processors of its own, does not call `AddCapar`, and references a project
  with a mediator generates nothing and reports nothing about its references, so the projects below an
  application layer no longer warn with CPR0005. It still generates when a handler, processor or generic handler
  in its references sits in a library no referenced mediator's project references, so that handler is not
  dropped without a word.
- Handlers in generated code are left out by the generator as well as the analyzer, by the compiler's rules for
  generated code, so the two agree; a request whose only handler is generated is reported with CPR0001.
- The migration analyzer and code fix ship in `Capar.Contracts`, so library projects get them too, and run from
  the command line with `dotnet format analyzers --diagnostics CPR0007 --severity info`.
- CPR0007 and its code fix, **Move from MediatR to Capar**: every piece of MediatR code left after the package
  swap, `using` directives, names, `MediatRServiceConfiguration` and `AddMediatR` calls, rewritten for Çapar
  with the scanning, licence and generic-closing settings removed. One "Fix all in solution" covers it all.
- CPR0006: an `AddCapar` call that binds to another project's generated method, which would register that
  project's handlers instead of this one's.
- Generic handlers, closed at build time: notification handlers declared as `H<T> : INotificationHandler<T>`
  over every notification the build sees, as MediatR's default does, and request and stream handlers over the
  types that satisfy their constraints, as MediatR's `RegisterGenericHandlers` does. CPR0004 now says why a
  generic handler could not be closed.
- A migration guide, and an order service on MediatR 12.5 next to the same service moved to Çapar; CI fails
  if the two print different results.

### Fixed

- `AddCapar` is declared in `Microsoft.Extensions.DependencyInjection`, so a top-level `Program.cs` finds it
  without a `using` for the project's namespace.
