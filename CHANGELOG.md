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
