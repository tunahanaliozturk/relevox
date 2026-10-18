# Diagnostics

Çapar decides dispatch while the project builds, so the mistakes a reflection-based mediator finds when a
request is first sent, usually in production, are reported by the compiler instead. Each rule below is
reported by the analyzer in the `Capar` package, at the declaration it is about, so `#pragma warning
disable` and `.editorconfig` severities work as they do for any other analyzer.

| Rule | Default | In short |
|---|---|---|
| [CPR0001](#cpr0001) | Warning | A request has no handler |
| [CPR0002](#cpr0002) | Error | A request has more than one handler |
| [CPR0003](#cpr0003) | Error | A handler cannot be called from the generated code |
| [CPR0004](#cpr0004) | Warning | A generic handler cannot be registered |
| [CPR0005](#cpr0005) | Warning | A referenced project has its own mediator |
| [CPR0006](#cpr0006) | Warning | An `AddCapar` call registers another project's mediator |

## CPR0001

**Request has no handler.** Warning.

A request or stream request exists and nothing in this project, or in the projects it references, handles
it. Sending it compiles and then throws `InvalidOperationException`, as it would with MediatR.

It is a warning rather than an error because a request often exists for a while before its handler does,
halfway through a change. A build that treats warnings as errors still stops on it.

**Fix:** add the handler, or, if it lives in a project this one does not reference, add the reference. If
the request is sent only from another application that has the handler, suppress the warning for that type:

```csharp
#pragma warning disable CPR0001 // handled by the billing service, which sends it over the bus
public sealed record ChargeCard(Guid OrderId) : IRequest<Receipt>;
#pragma warning restore CPR0001
```

## CPR0002

**Request has more than one handler.** Error.

A request is handled by exactly one handler. With two there is no right answer: MediatR keeps whichever its
assembly scan registered first, which depends on type order in metadata, and Çapar refuses to guess. Neither
handler is dispatched until the conflict is gone.

This includes a concrete handler and a concrete subclass of it, which MediatR registers as two handlers for
the same request.

**Fix:** remove one, or make the base handler `abstract`. If several pieces of code really should all react,
the message is a notification: implement `INotification` and `INotificationHandler<T>` instead.

## CPR0003

**Handler cannot be called from generated code.** Error.

The generated mediator is ordinary C# compiled into the project that calls `AddCapar`, so it can only call
handlers that project can see. The message says why this one cannot: the handler, or a type it handles, is
`internal` to another project, or `private`, or nested in a `private` type. A handler whose type, or whose
request or response type, is marked `[Obsolete]` as an error is reported here too, because calling it would
not compile.

**Fix:** make the handler `public`, or give the project that calls `AddCapar` access with
`[assembly: InternalsVisibleTo("YourApp")]` in the project that declares the handler.

## CPR0004

**Generic handler cannot be registered.** Warning.

A handler with type parameters of its own, such as `GetByIdHandler<T> : IRequestHandler<GetById<T>, T?>`,
is closed over real types at run time by MediatR. The generator writes dispatch for the types it can see
while building, so it cannot know which closed versions will be sent, and the handler is not registered.

**Fix:** make the generic handler `abstract` and declare one closed handler per request type:

```csharp
public abstract class GetByIdHandler<T>(Store store) : IRequestHandler<GetById<T>, T?>
{
    public Task<T?> Handle(GetById<T> request, CancellationToken cancellationToken) => store.FindAsync<T>(request.Id, cancellationToken);
}

public sealed class GetCustomerById(Store store) : GetByIdHandler<Customer>(store);
public sealed class GetOrderById(Store store) : GetByIdHandler<Order>(store);
```

Generic pre-processors, post-processors, exception handlers and actions whose type parameters are exactly
the interface's, such as `Logging<TRequest> : IRequestPreProcessor<TRequest>`, are not affected: they apply
to every request and are registered open, as MediatR registers them.

## CPR0005

**Referenced project has its own mediator.** Warning.

A project this one references also references the `Capar` package, so its build generated a mediator of its
own, and its handlers belong to that mediator rather than to this one. That is how a library uses Çapar
privately. The warning appears only when that project declares public handlers, which usually means it was
meant to contribute handlers to the application and referenced the wrong package.

**Fix:** in the referenced project, replace the `Capar` package with `Capar.Contracts`. Only the project that
calls `AddCapar`, usually the host, references `Capar`. If the library really does keep its own mediator,
make its handlers `internal` and the warning goes away.

## CPR0006

**AddCapar call registers another project's mediator.** Warning.

Every project that references the `Capar` package gets an `AddCapar` of its own, which registers that
project's mediator and the handlers it found. When another project's `AddCapar` is visible too, because that
project gives this one `InternalsVisibleTo`, a call can bind to the other one. It compiles, and this project's
handlers are never registered: the first request sent fails with "no handler".

The usual cases are a test project that references `Capar` and the application it tests, and a library that
uses Çapar privately and grants the host access to its internals.

**Fix:** call this project's method by its full name, which the message gives:

```csharp
global::Shop.Tests.CaparServiceCollectionExtensions.AddCapar(services);
```

If registering the other project's mediator is what you meant, suppress the warning at that call.
