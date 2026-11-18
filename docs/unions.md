# C# 15 union types

C# 15 has union types: `public union FindResult(Order, NotFound);` declares a type whose value is one of its
cases. Relevox's generator treats a union like any other type, so a union works as the response of a request, as
an item of a stream, as a request and as a notification, with nothing to turn on. This page says what an
application can rely on. Each statement here is a test in
[tests/Relevox.Unions.Tests](../tests/Relevox.Unions.Tests/UnionTests.cs), which CI runs on `net11.0`.

## What it needs

- `net11.0`. A union implements `System.Runtime.CompilerServices.IUnion`, which only .NET 11's runtime library
  has. The same code on `net10.0` with `LangVersion` 15.0 does not compile: `CS0518: Predefined type
  'System.Runtime.CompilerServices.IUnion' is not defined or imported`. Relevox cannot change that.
- C# 15. It is what the .NET 11 SDK uses for `net11.0` when the project does not set `LangVersion`; a project
  that pins an older one needs `<LangVersion>15.0</LangVersion>`.

A project that targets both frameworks keeps its unions in files compiled for `net11.0` alone, with C# 15 turned
on for that framework only. The NativeAOT sample does it that way, in
[its project file](../samples/Relevox.Samples.Aot/Relevox.Samples.Aot.csproj).

## A union response instead of a result library

Where a handler would return `OneOf<Order, NotFound>` or `ErrorOr<Order>`, it can return a union:

```csharp
public sealed record Order(int Id);
public sealed record NotFound;
public union FindResult(Order, NotFound);

public sealed record FindOrder(int Id) : IRequest<FindResult>;

public sealed class FindOrderHandler(OrderStore store) : IRequestHandler<FindOrder, FindResult>
{
    public Task<FindResult> Handle(FindOrder request, CancellationToken cancellationToken) =>
        Task.FromResult<FindResult>(store.Find(request.Id) is { } order ? order : new NotFound());
}
```

The caller gets the case the handler returned, and a `switch` over it needs no discard arm, so adding a case to
the union makes every such switch a compiler warning:

```csharp
FindResult result = await sender.Send(new FindOrder(id), cancellationToken);
return result switch
{
    Order order => Results.Ok(order),
    NotFound => Results.NotFound(),
};
```

The rest of the pipeline sees the same union:

- A behaviour closed over it, `IPipelineBehavior<FindOrder, FindResult>`, runs and can switch on the response.
- A stream, `IStreamRequest<FindResult>`, yields each item as the union.
- A generic union works the same way: `union Lookup<T>(T, NotFound)` as the response of
  `IRequest<Lookup<string>>`.
- `Send(object)` returns the union, boxed, not the case it holds. A union is a struct with the case inside it,
  so the object is a `FindResult`, and `response is Order` is false; unwrap it with `(FindResult)response` first.

The [NativeAOT sample](../samples/Relevox.Samples.Aot/Unions.cs) has this handler and its switch. CI publishes
that sample with NativeAOT and runs it, and on `net11.0` it checks the union response and a union notification.

## A union as a notification

A union can itself be a notification, over cases that may be notifications too:

```csharp
public sealed record Placed(int OrderId) : INotification;
public sealed record Cancelled(int OrderId) : INotification;
public union OrderEvent(Placed, Cancelled) : INotification;
```

Publishing an `OrderEvent` runs the handlers of `OrderEvent`, `INotificationHandler<OrderEvent>`. It does not
run a handler of the case it holds: with `OrderEvent cancelled = new Cancelled(5)`, both `Publish(cancelled)` and
`Publish((object)cancelled)` leave `INotificationHandler<Cancelled>` alone. Relevox dispatches on the type that
was published, and the union is a type of its own. Publishing `new Cancelled(5)` runs the handlers of
`Cancelled` and not those of the union. An application that wants both publishes both.

An open generic handler such as `AuditLog<T> : INotificationHandler<T> where T : INotification` is closed over
the union like over any other notification; the NativeAOT sample checks this too.

## A union as a request

`public union Command(Open, Close) : IRequest<string>;` is a request like any other, and its handler is
`IRequestHandler<Command, string>`. With `Relevox.FluentValidation`, a validator `AbstractValidator<Command>`
makes the build close the validation behaviour over the union, and `options.AddValidation()` runs it before the
handler.

## Testing and the message map

- `FakeMediator` answers a union request, `Setup<FindOrder, FindResult>(query => new Order(query.Id))`, and
  records a published union: `Published<OrderEvent>()` has it, and `Published<Cancelled>()` does not, for the
  same reason the case's handlers do not run.
- `NotificationRecorder` does the same in front of the real mediator: it records the union, not its case.
- The [message map](message-map.md) names the union as the response, `Relevox.Unions.Tests.FindResult`, and
  as a notification of its own with its own handlers. It does not list a union's cases.

## Not tried

Only the cases above have tests. Nothing tried so far needed a change in Relevox, and nothing found is
unsupported; a case that is not listed here may still work, but no test would notice if it stopped.
