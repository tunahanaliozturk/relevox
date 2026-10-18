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

**Generic notification handlers run after every other handler.** MediatR registers a handler declared as
`Audit<T> : INotificationHandler<T>` as an open generic, after everything its scan closed, and the container
closes it for each notification published, within its constraints. So it runs last, after catch-all
handlers, for every notification its constraints admit, including one nothing else handles. A closing does
not count as handling the notification for the catch-all rule above: a base-type handler still sees only
notifications some written handler names. Çapar closes such handlers at build time, over the notification
types the build can see.

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

**Handlers run in metadata order.** MediatR registers what its scan finds in metadata order, and that is the
order notification handlers, processors and exception handlers run in. For a referenced project that is its
TypeDef table, which Çapar reads directly. For the project being built it is what the compiler will emit: a
namespace's own types in declaration order, then its child namespaces, each in the order it was first
declared. A handler in a nested namespace therefore runs after the handlers of its parent namespace even when
its file comes first. Every trace in the differential tests is compared in order, not as a set.

## The pipeline

**Behaviours run outermost first, in registration order**, and are resolved from the container on every
send. Behaviours registered through `AddCapar`'s options and ones registered directly with the container,
before or after, all run. `next` can be called more than once, and a token passed to `next` replaces the
original from there inward; `next()` with no token keeps the token `Send` was given.

**A request without a response runs through behaviours typed over `Unit`**: `IPipelineBehavior<Ring, Unit>`.

**Processors only run when one was added through the options.** This is the most surprising rule the
differential tests found. MediatR 12 registers processors it finds when `AutoRegisterRequestProcessors` is
set, but it only adds the behaviour that runs them when at least one processor of that kind was added with
`AddRequestPreProcessor`, `AddOpenRequestPreProcessor` or their post-processor equivalents. Processors
found by scanning, or registered with the container directly, therefore never run on their own. Çapar does
exactly the same, so no processor starts running on the day you migrate. If yours never ran under MediatR,
this is why.

**The built-in behaviours sit outside everything else, exception behaviours outermost.** The order is the
exception behaviours (actions and handlers, in the order the strategy gives), then the pre-processor
behaviour, then the post-processor behaviour, then whatever the options and the container add. So an exception
handler catches what a pre-processor throws, and when a handler's exception is handled the post-processors do
not run.

**A processor, exception handler or exception action written for a base type or interface reaches a request
only when something else names that request exactly.** The same rule as catch-all notification handlers,
for the same reason.

**Exception handlers are tried from the thrown type up to `Exception`**, in metadata order at each step,
until one calls `SetHandled`. A handler for `Exception` also runs at the step of any more specific exception
type that some other handler names. A handler is tried once, at the most specific step that reaches it.
Unhandled exceptions are rethrown with their original stack, and so is one "handled" with a null response:
`SetHandled(null)` does not count.

**Exception actions see only unhandled exceptions by default**, and every exception with
`RequestExceptionActionProcessorStrategy.ApplyForAllExceptions`.

**Once any exception handler, exception action or processor behaviour exists, every send is asynchronous.**
In MediatR those behaviours apply to every request, and they are `async`, so a handler that throws before
its first `await` faults the returned task instead of throwing out of `Send`, for every request in the
application. Çapar leaves a behaviour with nothing to do out of the pipeline, for speed, and turns a
synchronous throw into a faulted task in its place, which costs a try block and nothing on the success
path, whether or not other behaviours are left. A request with no handler faults the task in that case too,
and when an open generic behaviour is registered, at any point.

**Stream behaviours are resolved when enumeration starts**, and their `next` takes no token.

**`TaskWhenAllPublisher` and custom publishers receive one executor per handler**, in metadata order, and
are called for a notification with no handlers too, with an empty list. With the default
`ForeachAwaitPublisher`, Çapar awaits the handlers itself without building executors; the behaviour is the
same. Which publisher is in force is read from the container on first use, so one registered after
`AddCapar` wins, as in MediatR.

**The configuration has every member of MediatR's** that still means something without assembly scanning, so
the lambda passed to `AddMediatR` compiles against `AddCapar` once the scanning calls are removed. A test
compares the two types member by member.

## Different on purpose

**Two handlers for one request is a build error (`CPR0002`).** That includes a concrete handler and a
concrete subclass of it. MediatR keeps whichever its scan registered first.

**Exception messages differ.** Only exception types are part of the contract.

**Generic request and stream handlers are registered without `RegisterGenericHandlers`.** MediatR registers
them only when that option is set; Çapar closes them at build time and cannot read the option. A send that
would have failed for want of a handler under MediatR's default works instead.

**A handler written for one closing wins over the generic one.** With `RegisterGenericHandlers`, MediatR
registers the closing after the written handler and the container returns the closing. Çapar keeps the
written one, which is also what MediatR does without the option.

**Behaviours do not run for a request nothing handles.** MediatR closes its open generic behaviours over
the request type and runs them before discovering that no handler exists. Çapar knows at build time that
no handler exists, cannot close a generic behaviour over a type it has no dispatch entry for, and fails
straight away. The exception type is the same; the behaviours' side effects are missing. The build has
already warned about such a request with `CPR0001`.

## Not supported

**Handlers the build cannot see.** A handler in an assembly loaded at run time is not registered. Handlers
the generator can see but cannot call, such as internal ones in another project, are reported as `CPR0003`.

**Generic handlers the build cannot close.** A generic handler is registered by closing it at build time
([ADR 0008](adr/0008-generic-handlers.md)). A generic notification handler reaches the notification types
the build can see: concrete ones declared in the application and the projects it references, and closed
generic ones some handler names. Publishing an `EntityCreated<Invoice>` that nothing names reaches no
generic handler, where MediatR's container would close one for it. Generic request and stream handlers are
closed only over types that satisfy a constraint naming a type in the solution; the rest are reported as
`CPR0004`.

**Exception handlers for exception types the build cannot see.** MediatR closes
`IRequestExceptionHandler<,,>` over every type in the thrown exception's hierarchy at run time. Çapar looks
up only the exception types some handler for that request names, which is what lets it run without
generating code at run time. An exception handler registered directly with the container for some other
exception type is not found. Open generic exception handlers and actions are closed over those same
exception types, plus `Exception`.
