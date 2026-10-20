# Testing code that uses Relevox

Code that sends requests through `ISender` or `IMediator` is usually tested in one of two ways: with the
mediator replaced, so the test controls what each request returns, or with the real mediator and the real
handlers, checking what came out the other side. `Relevox.Testing` has a helper for each.

```bash
dotnet add package Relevox.Testing
```

Reference it from test projects only. It depends on `Relevox` but not on its source generator, so a test
project gets no mediator of its own from it; the test project uses the application's, as before.

## A fake mediator

`FakeMediator` implements `IMediator`. It answers each request with what the test set up, and remembers every
request sent and notification published.

```csharp
FakeMediator mediator = new FakeMediator()
    .Setup((GetPrice query) => query.Sku == "bolt" ? 1.25m : 0m)
    .Setup((ArchiveOrder command) => { })
    .Throws<ChargeCard>(new TimeoutException("payments are down"));

var checkout = new CheckoutService(mediator);
await checkout.Complete(order);

mediator.Sent<ArchiveOrder>().ShouldHaveSingleItem();
mediator.Published<OrderCompleted>().Single().OrderId.ShouldBe(order.Id);
```

- **`Setup((TRequest request) => response)`** answers a request with a response. The lambda's parameter type
  says which request; the compiler checks that the response type matches the request's `IRequest<T>`. When the
  lambda returns a narrower type than the request declares, such as a `List<T>` for `IReadOnlyList<T>` or a
  non-null value for `string?`, give the type arguments: `Setup<FindName, string?>(query => "Ada")`.
- **`SetupAsync((TRequest request, CancellationToken token) => task)`** for an answer that needs to await, or
  to see the cancellation token the code under test passed.
- **`Setup((TRequest request) => ...)`** for a request without a response, which runs the lambda and
  answers with `Unit`.
- **`Throws<TRequest>(exception)`** fails the send with a faulted task, as a handler that throws would.
- **`SetupStream((TRequest request) => items)`** for a stream request.
- **`Sent<T>()`** and **`Published<T>()`** return what went out, in order, filtered by type. Use `object` for
  everything. **`Clear()`** forgets them and keeps the setups.

A request nobody set up fails with `InvalidOperationException` naming the request type. A fake that answered
with `default` would let the code under test carry on with a price of zero, and the test would fail somewhere
further away, or pass.

Setups are matched on the request's runtime type, the way the generated mediator dispatches.

### Coming from Moq or NSubstitute

The same test with Moq:

```csharp
var mediator = new Mock<IMediator>();
mediator.Setup(m => m.Send(It.Is<GetPrice>(q => q.Sku == "bolt"), It.IsAny<CancellationToken>())).ReturnsAsync(1.25m);
mediator.Setup(m => m.Send(It.IsAny<GetPrice>(), It.IsAny<CancellationToken>())).ReturnsAsync(0m);
// ...
mediator.Verify(m => m.Send(It.IsAny<ArchiveOrder>(), It.IsAny<CancellationToken>()), Times.Once);
mediator.Verify(m => m.Publish(It.Is<OrderCompleted>(n => n.OrderId == order.Id), It.IsAny<CancellationToken>()), Times.Once);
```

`Send` has three overloads and `Publish` two, so a setup or a verification against the wrong one matches
nothing, and the test either fails with no clue why or passes while checking nothing. The fake has one way in
per kind of request and records the object itself, so the assertions are ordinary ones on a list.

## Recording notifications from the real mediator

In an integration test the application's own handlers run, and it is still useful to know what was published.
`AddRelevoxNotificationRecorder` puts a recording publisher in front of the one in force:

```csharp
services.AddRelevox();
services.AddRelevoxNotificationRecorder();   // after AddRelevox, so it wraps the publisher the application chose

// ... run the scenario ...

var recorder = provider.GetRequiredService<NotificationRecorder>();
recorder.Published<OrderCompleted>().ShouldHaveSingleItem();
```

Every handler still runs, through the same publisher as before. A notification nobody handles is recorded too.

With `WebApplicationFactory`, register it in `ConfigureTestServices`, which runs after the application's own
registrations.

## Replacing one handler

For a test that keeps the real mediator but swaps one handler, register a replacement for the handler's class
before `AddRelevox`, as [compatibility.md](compatibility.md) describes:

```csharp
services.AddTransient<GetPriceHandler, FixedPrice>();   // FixedPrice derives from GetPriceHandler
services.AddRelevox();
```

The generated mediator resolves handlers by their class, so a replacement registered against
`IRequestHandler<GetPrice, decimal>` is not the one it calls.
