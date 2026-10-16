# Behaving like MediatR 12.5.0

Çapar is held to MediatR 12.5.0's observable behaviour by the
[differential tests](../tests/Capar.DifferentialTests): the same scenario source is compiled once against
MediatR and once against Çapar, and both must produce the same result, run the same handlers in the same
order, and throw the same exception type at the same moment.

This page lists the behaviours those tests pinned down that are easy to get wrong, and the places Çapar
differs on purpose.

## Matched, including the surprising parts

**A catch-all notification handler only sees notifications something else handles.** A handler for a base
type, or for `INotification` itself, receives a notification only when at least one handler names that
notification's exact type. A notification nobody handles directly reaches no handler at all. This is a
consequence of how MediatR registers contravariant handlers in Microsoft's container rather than a design
anyone would choose, and Çapar matches it so that a catch-all audit handler does not start seeing new
notifications the day you migrate.

**`Send(object)` for a request without a response returns a boxed `Unit`**, not null.

**A stream request with no handler fails when the stream is enumerated**, not when `CreateStream` is called.

**A request handler that throws before its first `await` throws out of `Send`**, synchronously. A
notification handler that does the same faults the task `Publish` returned instead.

**A request whose only handler is for its base type has no handler.** Sending a `DerivedQuery` when only
`IRequestHandler<BaseQuery, T>` exists throws `InvalidOperationException`, in both.

**Transient handlers are new on every call**, in both.

## Different on purpose

**Notification handlers run in a fixed order: by handler type name.** MediatR runs them in container
registration order, which follows its assembly scan and is documented as unspecified. The differential
tests compare the set of handlers that ran, not their order.

**Exception messages differ.** Only exception types are part of the contract.

## Not supported

**Handlers the build cannot see.** A handler in an assembly loaded at run time, or a generic handler closed
at run time, is not registered. The generator reports generic handlers as `CPR0004`.
