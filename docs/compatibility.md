# Behaving like MediatR 12.5.0

Relevox is held to MediatR 12.5.0's observable behaviour by the
[differential tests](../tests/Relevox.DifferentialTests): the same scenario source is compiled once against
MediatR and once against Relevox, and both must produce the same result, run the same handlers in the same
order, and throw the same exception type at the same moment.

This page lists the behaviours those tests pinned down that are easy to get wrong, and the places Relevox
differs on purpose.

## Project layout

Only the project that calls `AddRelevox`, usually the host, references the `Relevox` package. Projects that
declare requests and handlers reference `Relevox.Contracts`. The host's build finds the handlers in every
project it references, directly or not.

A referenced project that references `Relevox` itself gets its own generated mediator, and its handlers stay
with it. That is what lets a library use Relevox privately. If such a project has public handlers, the host's
build warns with `RVX0005`, because that is usually a project that meant to reference the contracts.

Analyzers flow through project references, so projects below the one that calls `AddRelevox`, such as an API or a
test project referencing the application layer, run the generator too. A project that has no handlers or
processors of its own, does not call `AddRelevox`, and references a project with a mediator generates nothing and
reports nothing about its references: it uses that project's mediator. The exception is a handler, a
processor or a generic handler in a contracts-only reference that no referenced mediator dispatches to, because
the mediator's project does not reference that library itself. Then the project generates as before, and
reports RVX0005, rather than drop that handler.

`AddRelevox` called a second time from the same project does nothing. Called after another project's
`AddRelevox`, it throws, because only one mediator can be `IMediator` and keeping the first silently would
leave the other's handlers unreachable. An `IMediator`, `ISender` or `IPublisher` the application registered itself before
`AddRelevox`, such as a test double, is kept, as MediatR keeps it.

## Matched, including the surprising parts

**A catch-all notification handler only sees notifications something else handles.** A handler for a base
type, or for `INotification` itself, receives a notification only when at least one handler names that
notification's exact type. A notification nobody handles directly reaches no handler at all. This is a
consequence of how MediatR registers contravariant handlers in Microsoft's container rather than a design
anyone would choose, and Relevox matches it so that a catch-all audit handler does not start seeing new
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
notifications some written handler names. Relevox closes such handlers at build time over the notification
types the build can see, and at run time, once per type, over a notification type it could not see, such as
`EntityCreated<Invoice>` built from a type parameter ([ADR 0009](adr/0009-open-notification-handlers-at-run-time.md)).

**Dispatch goes by the runtime type**, through every entry point, the typed overloads on the generated
mediator included. A `DerivedQuery` held in a `BaseQuery` variable is a `DerivedQuery`.

**`Lifetime` applies to the mediator, not the handlers.** Handlers stay transient unless
`HandlerLifetime` says otherwise, as with MediatR's own `Lifetime` setting.

A handler registered as a singleton or scoped is resolved once and held by the mediator. Which ones those are is
read from the service collection when the container first builds a mediator, so a handler registered again after
`AddRelevox` counts with its last registration, as the container resolves it. Adding registrations to a
collection after a container has been built from it, and building a second container, is not followed: the
Generic Host makes the collection read-only at that point, and Relevox reads it as it stands on first use.

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
TypeDef table, which Relevox reads directly. For the project being built it is what the compiler will emit: a
namespace's own types in declaration order, then its child namespaces, the one first declared last. A
handler in a nested namespace therefore runs after the handlers of its parent namespace even when
its file comes first. A handler class nested in another class comes after every top-level type in the
project, and one nested two deep after all of those. Every trace in the differential tests is compared in
order, not as a set.

## The pipeline

**Behaviours run outermost first, in registration order**, and are resolved from the container on every
send. Behaviours registered through `AddRelevox`'s options and ones registered directly with the container,
before or after, all run. `next` can be called more than once, and a token passed to `next` replaces the
original from there inward; `next()` with no token keeps the token `Send` was given.

**A request without a response runs through behaviours typed over `Unit`**: `IPipelineBehavior<Ring, Unit>`.

**Processors only run when one was added through the options.** This is the most surprising rule the
differential tests found. MediatR 12 registers processors it finds when `AutoRegisterRequestProcessors` is
set, but it only adds the behaviour that runs them when at least one processor of that kind was added with
`AddRequestPreProcessor`, `AddOpenRequestPreProcessor` or their post-processor equivalents. Processors
found by scanning, or registered with the container directly, therefore never run on their own. Relevox does
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
application. Relevox leaves a behaviour with nothing to do out of the pipeline, for speed, and turns a
synchronous throw into a faulted task in its place, which costs a try block and nothing on the success
path, whether or not other behaviours are left. A request with no handler faults the task in that case too,
and when an open generic behaviour is registered, at any point.

**Stream behaviours are resolved when enumeration starts**, and their `next` takes no token.

**`TaskWhenAllPublisher` and custom publishers receive one executor per handler**, in metadata order, and
are called for a notification with no handlers too, with an empty list. With the default
`ForeachAwaitPublisher`, Relevox awaits the handlers itself without building executors; the behaviour is the
same. Which publisher is in force is read from the container on first use, so one registered after
`AddRelevox` wins, as in MediatR.

**The configuration has every member of MediatR's** that still means something without assembly scanning, so
the lambda passed to `AddMediatR` compiles against `AddRelevox` once the scanning calls are removed. A test
compares the two types member by member.

## Different on purpose

**A handler is resolved by its own class, not through its interface.** The generated mediator asks the
container for `PlaceOrderHandler`, not for `IRequestHandler<PlaceOrder, int>` ([ADR 0004](adr/0004-resolve-per-call.md)).
A registration made against the interface, such as a test double or a decorator added with Scrutor's
`Decorate(typeof(IRequestHandler<,>), ...)`, is therefore not used, where MediatR would use it. Replace a
handler by registering its class before `AddRelevox` (`services.AddTransient<PlaceOrderHandler, FakePlaceOrder>()`,
with the fake deriving from it), and put cross-cutting work in a pipeline behaviour, which is resolved through
its interface as in MediatR.

Code of your own that takes a handler through its interface still works, as under MediatR: a handler that
injects `IRequestHandler<GetStock, int>` to call another handler directly, or a class that injects
`IEnumerable<INotificationHandler<OrderPlaced>>`. `AddRelevox` registers every handler under the interface it
implements, and a handler for a base type or an interface under each concrete notification it reaches, in the
order they run. Each of those registrations hands out the class registration, with its lifetime, so it is the
same instance the mediator would use, and that holds for an abstract or interface notification type as well.
One case still comes out shorter than MediatR's list: a generic notification handler is registered under its
interface only for the concrete notification types it was closed over at build time, where MediatR registers it
open and the container closes it for any type, abstract ones included.

**Two handlers for one request is a build error (`RVX0002`).** That includes a concrete handler and a
concrete subclass of it. MediatR keeps whichever its scan registered first.

**Exception messages differ.** Only exception types are part of the contract.

**The built-in behaviours are placeholders in the container.** MediatR registers its exception and processor
behaviours with the container, and resolving `IEnumerable<IPipelineBehavior<TRequest, TResponse>>` yourself
returns working ones. Relevox registers slots in their place, which the generated mediator replaces or leaves
out, and which throw `NotSupportedException` if something else runs them. Code that decorates or runs pipeline
behaviours itself, outside `ISender` and `IMediator`, sees the difference.

**Generic request and stream handlers are registered without `RegisterGenericHandlers`.** MediatR registers
them only when that option is set; Relevox closes them at build time and cannot read the option. A send that
would have failed for want of a handler under MediatR's default works instead.

**A handler written for one closing wins over the generic one.** With `RegisterGenericHandlers`, MediatR
registers the closing after the written handler and the container returns the closing. Relevox keeps the
written one, which is also what MediatR does without the option.

**Behaviours do not run for a request nothing handles.** MediatR closes its open generic behaviours over
the request type and runs them before discovering that no handler exists. Relevox knows at build time that
no handler exists, cannot close a generic behaviour over a type it has no dispatch entry for, and fails
straight away. The exception type is the same; the behaviours' side effects are missing. The build has
already warned about such a request with `RVX0001`.

## Not supported

**Handlers in generated code.** MediatR's scan finds every handler in the assembly. Relevox leaves out what the
compiler counts as generated code, the same code analyzers skip: files named `*.g.cs`, `*.designer.cs` or
`*.generated.cs`, files that open with an `<auto-generated>` comment, files marked `generated_code = true` in
.editorconfig, and types with `[GeneratedCode]`. A handler written by another source generator, such as a Razor
component that implements `INotificationHandler<T>`, is out of reach in any case, because one generator cannot
see another's output. Either way the build says so: a request whose only handler is generated gets `RVX0001`.
Move the handler into a file of your own, or set `generated_code = false` for that file.

**Behaviours added by a child scope to a request that had none.** Whether a request type has behaviours at
all is learned on its first send and kept for the container, which is right for Microsoft's container, whose
registrations are fixed once it is built. A container whose child scopes register behaviours of their own,
as Autofac's can, will not see them for a request that had none when it was first sent. A request that had
some is checked against each scope's list.

**Handlers the build cannot see.** A handler in an assembly loaded at run time is not registered. Handlers
the generator can see but cannot call, such as internal ones in another project, are reported as `RVX0003`.

**Generic handlers the build cannot close.** A generic handler is registered by closing it at build time
([ADR 0008](adr/0008-generic-handlers.md)). A generic notification handler is the exception: it is also
closed at run time over a notification type the build never saw ([ADR 0009](adr/0009-open-notification-handlers-at-run-time.md)),
except a struct under NativeAOT, which cannot be closed there. Generic request and stream handlers are closed
only over types that satisfy a constraint naming a type in the solution; the rest are reported as `RVX0004`.

**Exception handlers for exception types the build cannot see.** MediatR closes
`IRequestExceptionHandler<,,>` over every type in the thrown exception's hierarchy at run time. Relevox looks
up only the exception types some handler for that request names, which is what lets it run without
generating code at run time. An exception handler registered directly with the container for some other
exception type is not found. Open generic exception handlers and actions are closed over those same
exception types, plus `Exception`, or plus the class their exception type parameter is constrained to, such as
`DomainException` for `where TException : DomainException`; the container skips any closing that breaks the
constraint. Only requests that meet the request type parameter's constraints look them up. A subclass of that
class which no handler names, thrown by a request, reaches such a handler once, at the constrained class, where
MediatR runs it at the subclass and again at each class above it. A constraint no single exception class can
meet, such as an interface, is reported as `RVX0004`.
