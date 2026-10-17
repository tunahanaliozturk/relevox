# Changelog

All notable changes to this project are written down here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

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
- `eng/package-smoke.sh` and a CI job that pack both packages, install them into a new application, run it,
  and check that the packaged analyzer reports CPR0001.
- Package icon and a package README.
- A help page per diagnostic, linked from each rule.
- A migration guide, and an order service on MediatR 12.5 next to the same service moved to Çapar; CI fails
  if the two print different results.

### Fixed

- `AddCapar` is declared in `Microsoft.Extensions.DependencyInjection`, so a top-level `Program.cs` finds it
  without a `using` for the project's namespace.
