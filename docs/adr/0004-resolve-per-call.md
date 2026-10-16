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

## Alternatives considered

- **Cache handlers registered as singletons.** Possible as a later optimisation, decided per handler from
  its registered lifetime at startup. Not done until the per-call lookup shows up as the dominant cost.
