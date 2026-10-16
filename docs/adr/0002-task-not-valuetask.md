# 2. Handlers return Task, not ValueTask

Status: accepted, to be revisited after the M4 benchmarks
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
