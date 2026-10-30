# 24. Moving from the Mediator library: RVX0017 and its code fix

Status: accepted
Date: 2026-10-30

## Context

RVX0007 and "Move from MediatR to Relevox" take a project off MediatR in one build: swap the packages, run "Fix
all", build. The Mediator library (martinothamar/Mediator, 3.0.2 when this was written) has no such way in, and
1.7 is about giving it one (#145, #56). [ADR 0023](0023-no-valuetask-handlers.md) settled that its `ValueTask`
handlers become `Task` ones.

Mediator is further from Relevox than MediatR is. RVX0007 can mostly rename a namespace, because MediatR 12 and
Relevox share names and signatures. Mediator's public surface, read from `Mediator.Abstractions` 3.0.2 and the
code `Mediator.SourceGenerator` 3.0.2 generates, differs in four ways that a rename does not cover:

- Commands and queries: `ICommand<T>`, `IQuery<T>`, `IStreamCommand<T>`, `IStreamQuery<T>` and their handlers,
  beside `IRequest<T>`. Relevox has only requests.
- A void request or command is `IRequest<Unit>` underneath, and its handler returns `ValueTask<Unit>`, ending in
  `return Unit.Value;`.
- Handlers and behaviours return `ValueTask`. A behaviour's `next` is `MessageHandlerDelegate<TMessage,
  TResponse>`, called as `next(message, cancellationToken)`; Relevox's is `RequestHandlerDelegate<TResponse>`,
  called as `next(cancellationToken)`. A stream behaviour's `next(message, cancellationToken)` is `next()`.
- `services.AddMediator(options => ...)` configures `MediatorOptions`, whose `ServiceLifetime` is `Singleton` by
  default and applies to the mediator, the handlers and the behaviours alike. Relevox's are transient by default.

## Decision

**RVX0017, Info, on the same terms as RVX0007.** It is reported only where Relevox is referenced and
`Mediator.IMediator` does not resolve, that is once the Mediator packages are gone, so a project with both
referenced is never told to move. One id for every kind of leftover, so one "Fix all in solution" does the whole
move. It reports three kinds:

- **Name**: a name rooted at `Mediator`, in a using directive or a qualified name. The fix renames the root to
  `Relevox`, with the usings of the block kept in order, as RVX0007's fix does.
- **Type**: a class, record or struct whose base list names a Mediator type that does not exist in Relevox, or
  whose `Handle` method returns `ValueTask`. The fix rewrites the declaration as a whole, so a type is either moved
  or left untouched, never half moved.
- **Registration**: an `AddMediator` call.

**The type mapping.** Commands and queries become requests; nothing is lost, since Relevox dispatches by type and
Mediator's distinction is only in the names.

| Mediator | Relevox |
|---|---|
| `IRequest<T>`, `ICommand<T>`, `IQuery<T>` | `IRequest<T>` |
| `IRequest`, `ICommand` | `IRequest<Unit>` |
| `IRequestHandler<TReq, T>`, `ICommandHandler<TReq, T>`, `IQueryHandler<TReq, T>` | `IRequestHandler<TReq, T>` |
| `IRequestHandler<TReq>`, `ICommandHandler<TReq>` | `IRequestHandler<TReq, Unit>` |
| `IStreamRequest<T>`, `IStreamCommand<T>`, `IStreamQuery<T>` | `IStreamRequest<T>` |
| `IStreamRequestHandler<TReq, T>`, `IStreamCommandHandler<TReq, T>`, `IStreamQueryHandler<TReq, T>` | `IStreamRequestHandler<TReq, T>` |
| `INotification`, `INotificationHandler<T>` | the same |
| `IPipelineBehavior<TMessage, T>` | the same, `next` as above, an `IMessage` constraint replaced by `notnull` |
| `IStreamPipelineBehavior<TMessage, T>` | the same, `next` as above |

A void request keeps `Unit` rather than becoming Relevox's void `IRequest`: its handler's `return Unit.Value;`
stays valid, and a caller's `await sender.Send(command)` still gets a `Unit`. Relevox has its own `Unit`.

**Returns.** A `Handle` method's `ValueTask<T>` becomes `Task<T>` and `ValueTask` becomes `Task`. An `async` method
needs nothing else. In one that is not, the common completed returns are rewritten: `new ValueTask<T>(x)`, `new(x)`
and `ValueTask.FromResult(x)` to `Task.FromResult(x)`; `ValueTask.CompletedTask` and `default` (for `ValueTask`)
to `Task.CompletedTask`; `Unit.ValueTask` to `Task.FromResult(Unit.Value)`. Any other returned expression is left
for the compiler to point at, since only its type tells whether it needs `.AsTask()`, and that type is gone with
the package.

**Behaviours.** `next(message, cancellationToken)` becomes `next(cancellationToken)` only when its first argument
is the behaviour's own message parameter. A behaviour that passes a different message on cannot be expressed in
Relevox, whose pipeline carries one request; that call is left as it is and the compiler points at it.

**Registration.** `AddMediator()` and `AddMediator(options => ...)` become `AddRelevox(...)`, keeping what Mediator
did:

- The lifetime, `Singleton` unless `ServiceLifetime` says otherwise, is set as both `Lifetime` and
  `HandlerLifetime`, and passed to each behaviour's registration. Moving to Relevox's transient default would be
  safe, since nothing that worked as a singleton breaks as a transient, but it would be a change nobody asked for,
  made silently. The fix writes it out so it can be seen and changed.
- `PipelineBehaviors` and `StreamPipelineBehaviors`, given as an array or collection of `typeof(...)`, become one
  `AddOpenBehavior` or `AddBehavior` call each (`AddOpenStreamBehavior` or `AddStreamBehavior` for streams), open
  for an unbound generic such as `typeof(Logging<,>)`.
- `NotificationPublisherType = typeof(ForeachAwaitPublisher)` or `typeof(TaskWhenAllPublisher)` becomes the same
  setting naming Relevox's publisher of that name.
- `Namespace`, `GenerateTypesAsInternal` and `Assemblies` are dropped: Relevox finds handlers at build time
  across the project and its references, and generates nothing an application names.
- Anything else, an options object passed in or a custom notification publisher, gets no fix.

## Consequences

- The common shapes of a Mediator project move in one "Fix all". What is not rewritten is listed in
  `docs/migration-from-mediator.md` (#146) with the change to make by hand: `MessagePreProcessor`,
  `MessagePostProcessor` and `MessageExceptionHandler` subclasses, a custom `INotificationPublisher`,
  `[assembly: MediatorOptions]`, variables and fields typed `ValueTask` at call sites, and the returns above.
- The fix is syntactic, as it has to be: once the package is removed nothing of Mediator's resolves. It matches
  Mediator's names, so it is limited to code that uses them as Mediator does, and it is tested shape by shape
  against a stand-in compiled with those names.
- A synchronous handler now allocates its `Task`, as ADR 0023 describes.
- Two migration analyzers now share a pattern. If a third arrives, their reporting and the usings rewrite are worth
  pulling into one place; for two, the duplication is smaller than the abstraction.

## Alternatives considered

- **Map void requests to Relevox's void `IRequest`.** Closer to idiomatic Relevox, but every void handler's body
  would have to lose its `return Unit.Value;` and callers that use the `Unit` would break. A later cleanup can do it
  in one more fix if anyone wants it.
- **Keep Mediator's commands and queries as Relevox interfaces.** More types in the contracts for a distinction
  Relevox does not act on. Teams that want the vocabulary can declare `ICommand<T> : IRequest<T>` themselves.
- **One diagnostic per Mediator name, like RVX0007.** A handler then gets its base list fixed separately from its
  `Handle` method, and a fix applied to one without the other leaves code that compiles against neither library.
  Reporting the declaration makes each type one change.
- **Run the fix while Mediator is still referenced, using its symbols.** It would know every type, but a project
  half way through a move would be told to move everything at once, which is what the bridge in 1.6 exists to
  avoid for MediatR. The same condition as RVX0007 keeps both migrations alike.
