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

## Second amendment, after the differential tests

Two things the M1 review and the differential tests settled:

- **What is kept is decided per handler, from the registration in force.** `AddRelevox` adds handlers with
  `TryAdd`, so a handler the application registered itself keeps the application's lifetime. The generated
  registration reads each handler's last descriptor and keeps the instance only when it is scoped or
  singleton; a transient registration is never kept, whatever the options say. Within one mediator
  instance a scoped handler is as safe to keep as a singleton, because a mediator never outlives its scope.
  Registrations made after `AddRelevox` are not seen, which the documentation says.
- **`Lifetime` is the mediator's lifetime only.** The differential tests showed that MediatR 12's
  `Lifetime` setting leaves handlers transient. Relevox matches that, and adds `HandlerLifetime` for the
  handlers, transient by default. Setting it to singleton is how a user gets Mediator-style numbers on
  purpose rather than by surprise.

## Third amendment, 1.0.1: registered under the interfaces too

A real migration failed at startup: one handler took another as `IRequestHandler<,>`, which MediatR
registers and Relevox did not. Handlers are now also registered under the interfaces MediatR registers them
under. Those registrations are factories that hand out the class registration, with its lifetime, so the
send path is unchanged: the mediator still asks for the class and the container still does no open-generic
work on a send. What changed is the sentence "never as `IRequestHandler<,>`": it now holds for resolving, not
for registering.

## Alternatives considered

- **Singleton by default, as Mediator does.** Fastest out of the box, and it turns every handler with a
  scoped dependency into a captive dependency in codebases that were written for MediatR's transient
  default. Opt-in instead.
