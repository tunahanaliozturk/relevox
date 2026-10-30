# 2. Handlers return Task, not ValueTask

Status: accepted, confirmed after the M4 benchmarks (2026-10-17)
Date: 2026-10-16

## Context

`ValueTask<T>` lets a handler that completes synchronously return without allocating. Mediator and
fastcast both use it. MediatR uses `Task<T>`, and so does every handler in every codebase that would move
to this library.

## Decision

Keep `Task<T>` and `Task`, with MediatR's method names and parameter order.

## Consequences

- Migrating is a namespace and package change. No handler signature moves, which for a codebase with a few
  hundred handlers is the difference between an afternoon and a project.
- A handler returning `Task.FromResult(x)` allocates a task per call. That allocation is the handler's, not
  the library's: the no-behaviour path hands the handler's task straight back without awaiting it, so the
  library itself adds nothing. Handlers that care can return a cached task.
- If the M4 benchmarks show the `Task` shape itself costs more than its compatibility is worth, this gets
  revisited with numbers, and the likely answer would be an additional `ValueTask` handler interface rather
  than replacing this one.

## Alternatives considered

- **ValueTask only.** Faster in the synchronous case, and it breaks every existing handler. That is the
  migration cost this library exists to remove.
- **Both, chosen per handler.** Possible later. Two handler shapes double the generator's surface and the
  test matrix, so it waits for evidence that anyone needs it.

## Revisited after M4

The M4 numbers did not make the case for a `ValueTask` interface. With transient handlers in both libraries,
which is the default in MediatR and Relevox, Relevox's typed send takes 9.5 ns and allocates 24 B, the handler
itself; Mediator 3.0, whose handlers return `ValueTask`, takes 28.3 ns and allocates 88 B. Where Mediator is
faster, the difference comes from holding handlers as singletons, which Relevox also does when asked to. The
`Task` a synchronous handler allocates is real, and small next to what both libraries spend elsewhere. See
[the M4 results](../benchmark-results/2026-10-17-m4.md).

## Revisited for 1.7

1.7 planned a `ValueTask` handler interface next to this one, on condition that it made a send faster. A probe
of the send's shape showed it cannot while `ISender.Send` returns `Task`: a synchronous `ValueTask` handler took
18.1 ns against 7.9 ns, and the send still allocated the same 72 B. The interface was dropped;
[ADR 0023](0023-no-valuetask-handlers.md) has the numbers and what would have to change for a `ValueTask` path to
pay.
