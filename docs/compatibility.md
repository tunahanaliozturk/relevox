# Behaving like MediatR 12.5.0

Çapar is held to MediatR 12.5.0's observable behaviour by the
[differential tests](../tests/Capar.DifferentialTests): the same scenario source is compiled once against
MediatR and once against Çapar, and both must produce the same result, run the same handlers in the same
order, and throw the same exception type at the same moment.

This page lists the behaviours those tests pinned down that are easy to get wrong, and the places Çapar
differs on purpose.

## Project layout

Only the project that calls `AddCapar`, usually the host, references the `Capar` package. Projects that
declare requests and handlers reference `Capar.Contracts`. The host's build finds the handlers in every
project it references, directly or not.

A referenced project that references `Capar` itself gets its own generated mediator, and its handlers stay
with it. That is what lets a library use Çapar privately. If such a project has public handlers, the host's
build warns with `CPR0005`, because that is usually a project that meant to reference the contracts.

`AddCapar` called a second time from the same project does nothing. Called after another project's
`AddCapar`, it throws, because only one mediator can be `IMediator` and keeping the first silently would
leave the other's handlers unreachable.

## Matched, including the surprising parts

**A catch-all notification handler only sees notifications something else handles.** A handler for a base
type, or for `INotification` itself, receives a notification only when at least one handler names that
notification's exact type. A notification nobody handles directly reaches no handler at all. This is a
consequence of how MediatR registers contravariant handlers in Microsoft's container rather than a design
anyone would choose, and Çapar matches it so that a catch-all audit handler does not start seeing new
notifications the day you migrate.

**A struct notification never reaches an interface catch-all handler.** Variance does not apply to value
types.

**Closed generic notifications work.** A handler for `INotificationHandler<EntityCreated<Order>>` receives
`EntityCreated<Order>`.

**Dispatch goes by the runtime type**, through every entry point, the typed overloads on the generated
mediator included. A `DerivedQuery` held in a `BaseQuery` variable is a `DerivedQuery`.

**`Lifetime` applies to the mediator, not the handlers.** Handlers stay transient unless
`HandlerLifetime` says otherwise, as with MediatR's own `Lifetime` setting.

**`Send(object)` for a request without a response returns a boxed `Unit`**, not null. For something that is
not a request it throws `ArgumentException`; for a request with no handler it returns a faulted task.

**Streams start when they are enumerated.** The handler is resolved when enumeration begins, so a stream
enumerated twice gets a fresh transient handler each time, and a stream request with no handler fails on
enumeration. The token the handler receives includes the one passed to `WithCancellation`, even for a
handler that does not declare `[EnumeratorCancellation]`. `CreateStream(object)` throws `ArgumentException`
straight away for something that is not a stream request.

**Notification handlers are all resolved before the first one runs.** A handler that cannot be constructed
throws out of `Publish` before any handler has run. A handler that throws while handling faults the
returned task instead.

**A request handler that throws before its first `await` throws out of `Send`**, synchronously.

**A request whose only handler is for its base type has no handler.** Sending a `DerivedQuery` when only
`IRequestHandler<BaseQuery, T>` exists throws `InvalidOperationException`, in both.

## Different on purpose

**Notification handlers run in a fixed order: by handler type name.** MediatR runs them in container
registration order, which follows its assembly scan and is documented as unspecified. The differential
tests compare the set of handlers that ran, not their order.

**Two handlers for one request is a build error (`CPR0002`).** That includes a concrete handler and a
concrete subclass of it. MediatR keeps whichever its scan registered first.

**Exception messages differ.** Only exception types are part of the contract.

## Not supported

**Handlers the build cannot see.** A handler in an assembly loaded at run time, or a generic handler closed
at run time, is not registered. The generator reports generic handlers as `CPR0004`, and handlers it can
see but cannot call, such as internal ones in another project, as `CPR0003`.
