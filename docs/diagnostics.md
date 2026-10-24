# Diagnostics

Relevox decides dispatch while the project builds, so the mistakes a reflection-based mediator finds when a
request is first sent, usually in production, are reported by the compiler instead. Each rule below is
reported by an analyzer, at the code it is about, so `#pragma warning disable` and `.editorconfig` severities
work as they do for any other analyzer. RVX0001 to RVX0006 come with the generator in the `Relevox` package;
RVX0007 and the architecture rules from RVX0008 on come in `Relevox.Contracts`, so they also run in the libraries
that hold most handlers and reference only the contracts.

| Rule | Default | In short |
|---|---|---|
| [RVX0001](#rvx0001) | Warning | A request has no handler |
| [RVX0002](#rvx0002) | Error | A request has more than one handler |
| [RVX0003](#rvx0003) | Error | A handler cannot be called from the generated code |
| [RVX0004](#rvx0004) | Warning | A generic handler cannot be registered |
| [RVX0005](#rvx0005) | Warning | A referenced project has its own mediator |
| [RVX0006](#rvx0006) | Warning | An `AddRelevox` call registers another project's mediator |
| [RVX0007](#rvx0007) | Info | MediatR code to move to Relevox |
| [RVX0008](#rvx0008) | Info | A handler sends another request |
| [RVX0009](#rvx0009) | Info | Handlers send each other in a cycle |

## Architecture rules

From RVX0008 on, the rules are about how an application uses its mediator rather than about whether it can
dispatch. Each points at code that works and is often a mistake, so each is Info by default: an IDE shows it
as a suggestion, and a command-line build gets no new warning from a Relevox upgrade, so one with warnings as
errors keeps building ([ADR 0018](adr/0018-architecture-rule-defaults.md)). To make one fail the build, raise it in `.editorconfig`;
to silence one, lower it:

```ini
[*.cs]
# A send cycle fails the build.
dotnet_diagnostic.RVX0009.severity = error
# Handlers that send other requests are a pattern this code base uses on purpose.
dotnet_diagnostic.RVX0008.severity = none
```

`dotnet_analyzer_diagnostic.category-Relevox.severity` sets every Relevox rule at once, the dispatch rules too.

## RVX0001

**Request has no handler.** Warning.

A request or stream request exists and nothing in this project, or in the projects it references, handles
it. Sending it compiles and then throws `InvalidOperationException`, as it would with MediatR.

It is a warning rather than an error because a request often exists for a while before its handler does,
halfway through a change. A build that treats warnings as errors still stops on it.

**Fix:** add the handler, or, if it lives in a project this one does not reference, add the reference. If
the request is sent only from another application that has the handler, suppress the warning for that type:

```csharp
#pragma warning disable RVX0001 // handled by the billing service, which sends it over the bus
public sealed record ChargeCard(Guid OrderId) : IRequest<Receipt>;
#pragma warning restore RVX0001
```

## RVX0002

**Request has more than one handler.** Error.

A request is handled by exactly one handler. With two there is no right answer: MediatR keeps whichever its
assembly scan registered first, which depends on type order in metadata, and Relevox refuses to guess. Neither
handler is dispatched until the conflict is gone.

This includes a concrete handler and a concrete subclass of it, which MediatR registers as two handlers for
the same request.

**Fix:** remove one, or make the base handler `abstract`. If several pieces of code really should all react,
the message is a notification: implement `INotification` and `INotificationHandler<T>` instead.

## RVX0003

**Handler cannot be called from generated code.** Error.

The generated mediator is ordinary C# compiled into the project that calls `AddRelevox`, so it can only call
handlers that project can see. The message says why this one cannot: the handler, or a type it handles, is
`internal` to another project, or `private`, or nested in a `private` type. A handler whose type, or whose
request or response type, is marked `[Obsolete]` as an error is reported here too, because calling it would
not compile. So is a handler written as a `struct`: handlers are created by the container and held as
objects, so they have to be classes.

**Fix:** make the handler a `class` if it is a `struct`. Otherwise make it `public`, or give the project that calls `AddRelevox` access with
`[assembly: InternalsVisibleTo("YourApp")]` in the project that declares the handler.

## RVX0004

**Generic handler cannot be registered.** Warning.

The generated code calls closed types, so a generic handler is registered by closing it while the project
builds ([ADR 0008](adr/0008-generic-handlers.md)):

- A notification handler declared as `Audit<T> : INotificationHandler<T>` is closed over every notification
  type the build can see that satisfies its constraints, and over any other at run time, as MediatR's container
  closes it ([ADR 0009](adr/0009-open-notification-handlers-at-run-time.md)).
- A request or stream handler such as `GetByIdHandler<T> : IRequestHandler<GetById<T>, string>
  where T : IEntity` is closed over every type in the solution that satisfies its constraints, as MediatR does
  with `RegisterGenericHandlers`.
- A generic processor, exception handler or exception action whose type parameters are exactly the
  interface's, such as `Logging<TRequest> : IRequestPreProcessor<TRequest>`, is registered open, as MediatR
  registers it.

The message says which of these a handler missed:

| The message says | Because |
|---|---|
| has no constraint naming a type declared in this solution | An unconstrained type parameter, or one constrained only to a type from .NET or a NuGet package such as `IComparable<T>`, would mean closing it over far too many types. |
| no type in this solution satisfies its constraints | Nothing to close it over yet. |
| more than the 256 type combinations the generator will write | The constraints admit too many types, or too many combinations of them. |
| declared as H&lt;T&gt; : INotificationHandler&lt;T&gt;, with one type parameter | The notification is built from the type parameter, as in `INotificationHandler<Envelope<T>>`, or the handler has more type parameters than the notification. MediatR throws on every publish for the first shape. |
| registered only when its type parameters are exactly the interface's | A generic processor, exception handler or action such as `Pre<T> : IRequestPreProcessor<Wrapper<T>>`, which the container cannot close. |
| nested in a generic type | The handler's own type parameters come from the type around it. |
| constrained in a way only one accessible exception class can be | A generic exception handler or action whose exception type parameter is constrained to an interface, to a value type, to `new()`, or to more than one type, such as `where TException : Exception, IRetryable`. Relevox closes open exception handlers and actions over `Exception`, or over the one exception class they are constrained to (`where TException : DomainException`), and nothing in a thrown exception's chain of classes can meet these. Write one closed handler per exception type instead, or constrain the type parameter to a single exception class. |

An inaccessible generic handler, such as a `private` nested one or an `internal` one in another project, is
reported as [RVX0003](#rvx0003), like a written one.

**Fix:** constrain the type parameter to an interface or base class declared in the solution, or make the
generic handler `abstract` and declare one closed handler per request:

```csharp
public abstract class GetByIdHandler<T>(Store store) : IRequestHandler<GetById<T>, T?>
{
    public Task<T?> Handle(GetById<T> request, CancellationToken cancellationToken) => store.FindAsync<T>(request.Id, cancellationToken);
}

public sealed class GetCustomerById(Store store) : GetByIdHandler<Customer>(store);
public sealed class GetOrderById(Store store) : GetByIdHandler<Order>(store);
```

## RVX0005

**Referenced project has its own mediator.** Warning.

A project this one references also references the `Relevox` package, so its build generated a mediator of its
own, and its handlers belong to that mediator rather than to this one. That is how a library uses Relevox
privately. The warning appears only when that project declares public handlers, which usually means it was
meant to contribute handlers to the application and referenced the wrong package.

**Fix:** in the referenced project, replace the `Relevox` package with `Relevox.Contracts`. Only the project that
calls `AddRelevox`, usually the host, references `Relevox`. If the library really does keep its own mediator,
make its handlers `internal` and the warning goes away.

## RVX0006

**AddRelevox call registers another project's mediator.** Warning.

Every project that references the `Relevox` package gets an `AddRelevox` of its own, which registers that
project's mediator and the handlers it found. When another project's `AddRelevox` is visible too, because that
project gives this one `InternalsVisibleTo`, a call can bind to the other one. It compiles, and this project's
handlers are never registered: the first request sent fails with "no handler".

The usual cases are a test project that references `Relevox` and the application it tests, and a library that
uses Relevox privately and grants the host access to its internals.

**Fix:** call this project's method by its full name, which the message gives:

```csharp
global::Shop.Tests.RelevoxServiceCollectionExtensions.AddRelevox(services);
```

If registering the other project's mediator is what you meant, suppress the warning at that call.

## RVX0007

**MediatR code to move to Relevox.** Info.

Reported once the MediatR package is gone, on each piece of MediatR code the compiler can no longer resolve: a
name rooted at `MediatR`, in a `using` directive, an alias, a `using static`, a type or an expression such as
`MediatR.Unit.Value`; `MediatRServiceConfiguration`; and every `AddMediatR` call. Nothing is reported while the
MediatR package is still referenced, so a project can move over when it is ready.

The compiler already reports the same code as errors, under several ids. RVX0007 gives it one, so that the code
fix **Move from MediatR to Relevox** can be applied to all of it with a single *Fix all occurrences in: Solution*.

**Fix:** apply the code fix. It renames `MediatR` to `Relevox` and `MediatRServiceConfiguration` to `RelevoxOptions`,
which lives in `Microsoft.Extensions.DependencyInjection` as MediatR's configuration does, writes
`RequestExceptionActionProcessorStrategy` as `Relevox.RequestExceptionActionProcessorStrategy`, drops a
`using Relevox;` the rename would repeat,
and turns `AddMediatR` into `AddRelevox`, removing the settings that only drove MediatR's assembly scan, its licence
check or its run-time closing of generic handlers, and keeping comments and `#if` blocks around them. A comment
at the end of a removed line goes with it, since it was about that line. It leaves
alone, for a person to decide:

- an options object built before the call and passed to `AddMediatR`, which `AddRelevox` cannot take;
- names in MediatR namespaces Relevox has no counterpart for, such as `MediatR.Registration`;
- a namespace of your own called `MediatR`, which resolves and so is not reported: rename it first;
- `MediatR` names in XML documentation `cref`s.

## RVX0008

**Handler sends another request.** Info.

Reported at each `Send` or `CreateStream` call made on `ISender`, `IMediator` or the generated mediator from inside
a handler: a request, stream or notification handler, including lambdas and other methods of the handler class.
`Publish` is not reported; raising events from a handler is what notifications are for.

A request sent from a handler goes through a pipeline of its own inside the pipeline of the request being
handled. Every behaviour runs again, so a transaction behaviour opens a second transaction, a logging behaviour
logs twice, and a validation failure deep inside comes out of the outer `Send`. A chain of these is hard to
follow, and a loop between handlers is found only when the stack overflows (RVX0009).

**Fix:** move the work both handlers need into a service they both call, and keep `Send` for the edges of the
application: controllers, endpoints, message consumers and jobs. Where a handler sends on purpose, suppress the
rule there. eShop's `IdentifiedCommandHandler` is an example: it wraps a command for idempotency and sends the
command inside, so its pipeline runs for both on purpose ([docs/migration-trial.md](migration-trial.md)).

```csharp
#pragma warning disable RVX0008 // The wrapped command goes through its own pipeline on purpose.
R result = await _mediator.Send(command, cancellationToken);
#pragma warning restore RVX0008
```

## RVX0009

**Handlers send each other in a cycle.** Info.

Reported at a send from a handler when the request sent leads, through the handlers in this project, back to
the request being handled: `A` sends `B`, `B` sends `A`; or `A` sends itself. The message shows the shortest way
back, such as `A -> B -> C -> A`, and each send in the cycle is reported once.

Unless something on the way stops it, such as a depth that runs out, the sends recurse until the stack
overflows. Walking a tree by sending a request per node is the usual case where they do stop.

Only sends whose message type is known at build time are followed, and only to handlers in the same project: a
send of a type parameter, of `object`, or to a handler in a referenced project ends the search. In a class that
handles more than one message, a send inside one `Handle` counts from that request, or from none when it is a
notification's `Handle`; a send elsewhere in the class, a helper method called `Handle` included, counts from
every request it handles.

**Fix:** break the cycle, usually by moving the shared work into a service (RVX0008). If the recursion is
bounded on purpose, suppress the rule at the send. Raise it to `error` in `.editorconfig` to make any new cycle
fail the build.
