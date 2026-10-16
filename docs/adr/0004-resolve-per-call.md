# 4. Resolve handlers per call, by concrete type

Status: accepted
Date: 2026-10-16

## Context

The fastest handler is one resolved once and kept. The generated mediator could hold every handler in a
field. It would also be wrong for every handler registered as scoped, which in practice means every handler
that takes a `DbContext`.

## Decision

Resolve the handler, and each behaviour, from the current `IServiceProvider` on every call, as MediatR does.
Register and resolve them by their concrete type, never as `IRequestHandler<,>`, so the container never
performs open-generic resolution on the send path.

## Consequences

- Lifetimes mean what they meant under MediatR. A scoped handler gets the scope it was sent from.
- One container lookup per handler per call stays in every measurement. That is the floor for this design,
  and the benchmark tables say so.
- Concrete-type registration means a handler is visible in the container under its own type, which is also
  what makes it straightforward to replace in tests.

## Amendment, same day: cache singletons

The [baseline](../benchmark-results/2026-10-16-baseline.md) showed Mediator 3.0 sending in 1.2 ns with its
default singleton handlers, against MediatR's 36 ns with transient ones. The difference is almost entirely
that Mediator never asks the container. So the singleton case is part of v1: at startup the generated
code reads each handler's and behaviour's registered lifetime from the service descriptors, and the ones
registered as singletons are resolved once and held. Transient and scoped registrations keep the per-call
resolution above. The default lifetime stays transient, as in MediatR.

## Alternatives considered

- **Singleton by default, as Mediator does.** Fastest out of the box, and it turns every handler with a
  scoped dependency into a captive dependency in codebases that were written for MediatR's transient
  default. Opt-in instead.
