# Diagnostics

Relevox decides dispatch while the project builds, so the mistakes a reflection-based mediator finds when a
request is first sent, usually in production, are reported by the compiler instead. Each rule below is
reported by the analyzer in the `Relevox` package, at the declaration it is about, so `#pragma warning
disable` and `.editorconfig` severities work as they do for any other analyzer.

| Rule | Default | In short |
|---|---|---|
| [RVX0001](#rvx0001) | Warning | A request has no handler |
| [RVX0002](#rvx0002) | Error | A request has more than one handler |
| [RVX0003](#rvx0003) | Error | A handler cannot be called from the generated code |
| [RVX0004](#rvx0004) | Warning | A generic handler cannot be registered |
| [RVX0005](#rvx0005) | Warning | A referenced project has its own mediator |
| [RVX0006](#rvx0006) | Warning | An `AddRelevox` call registers another project's mediator |
| [RVX0007](#rvx0007) | Info | MediatR code to move to Relevox |

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
not compile.

**Fix:** make the handler `public`, or give the project that calls `AddRelevox` access with
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
| constrained to something narrower than Exception | A generic exception handler or action such as `DomainHandler<TRequest, TResponse, TException> where TException : DomainException`. Relevox registers open exception handlers and actions for `Exception` only, which such a handler cannot take. Write one closed handler per exception type instead, or constrain the type parameter to `Exception`. |

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
