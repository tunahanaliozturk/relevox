# 23. No ValueTask handlers in 1.7

Status: accepted
Date: 2026-10-30

## Context

The roadmap put `ValueTask` handlers in 1.7, beside a code fix for projects on the Mediator library, whose
handlers return `ValueTask` (#56). Two conditions came with it: `ISender` stays as it is, returning `Task`, and
the feature is dropped unless a send to a `ValueTask` handler is measurably faster than to a `Task` one (#143).
[ADR 0002](0002-task-not-valuetask.md) had already chosen `Task` and left the door open to a second interface if
numbers asked for one.

The question can be answered before touching the generator, because the cost is in the shape of the send, not in
how it is generated. A probe reproduces that shape by hand: a `Task<T>` send to a `Task` handler, as today; a
`Task<T>` send to a `ValueTask` handler, which is what 1.7 would have added; and a `ValueTask<T>` send to a
`ValueTask` handler, which `ISender` does not have. The full results and the probe's code are in
[2026-10-30-valuetask-probe.md](../benchmark-results/2026-10-30-valuetask-probe.md).

| Send | Handler completes | Mean | Allocated |
|---|---|---:|---:|
| `Task<T>` send, `Task` handler (today) | synchronously | 7.9 ns | 72 B |
| `Task<T>` send, `ValueTask` handler | synchronously | 18.1 ns | 72 B |
| `ValueTask<T>` send, `ValueTask` handler | synchronously | 3.2 ns | 0 B |
| `Task<T>` send, `Task` handler (today) | after `Task.Yield` | 1,167 ns | 206 B |
| `Task<T>` send, `ValueTask` handler | after `Task.Yield` | 1,166 ns | 220 B |

Behind a send that returns `Task`, a `ValueTask` handler cannot save anything. When it completes synchronously
the send has to turn its result into a `Task` anyway, so the 72 B stay and the extra check costs time. When it
completes asynchronously, the state machine allocates either way. The only shape that gains is a send that
returns `ValueTask` itself, and only for handlers that complete synchronously.

## Decision

**Handlers stay on `Task`, and 1.7 adds no `ValueTask` handler interface.** The feature fails the condition it
was planned under, so it is dropped, as #56 said it would be. #144 is closed as not planned.

**1.7 is about moving from Mediator.** The code fix in #145 rewrites Mediator's `ValueTask` handlers to `Task`
ones, which is a signature change per handler, done by the fix rather than by hand.

## Consequences

- One handler shape, so the generator, RVX0002 and the test matrix stay as they are, and package validation has
  nothing new to guard.
- A project coming from Mediator pays for a `Task` on a synchronous send where it did not before: 72 B for an
  `int` result, the same as every MediatR project already pays. Not every such send allocates: `Task.FromResult`
  hands back a cached task for `true`, `false`, `null`, the `int` values -1 to 8 and the default of value types
  such as `long`, `double`, `decimal` and `Guid` (checked on .NET 10.0.13 with `ReferenceEquals` on two calls),
  and a handler can return a task it keeps, such as `Task.CompletedTask`. The probe offsets its results to stay
  out of that cache, so its 72 B is the uncached case. `docs/migration-from-mediator.md` (#146) says so.
- Code that sends in a tight loop to a handler that completes synchronously cannot get to zero allocations
  through Relevox. If that turns out to matter to someone, the answer is a `ValueTask`-returning send next to
  `ISender.Send` with its own interfaces, as a separate proposal; nothing in this decision blocks it.

## Alternatives considered

- **`ValueTask` handlers behind the existing send.** What 1.7 planned. Slower when synchronous, equal when
  asynchronous, and a second handler shape to support: all cost and no gain.
- **A `ValueTask`-returning send as well.** The only variant that is faster, by about 5 ns and 72 B per
  synchronous send. It adds a public send method, a second set of handler interfaces and the question of which
  one a behaviour wraps, against the condition the roadmap set. Left for a proposal with a user who needs it.
- **Wait for a full run on an idle machine.** The probe ran on a machine that was not idle, and its standard
  deviations show it. The decision does not rest on a few nanoseconds, though: the planned variant cannot drop the
  allocation, which is structural, not noise.
