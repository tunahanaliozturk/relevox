# Relevox.Testing

Test helpers for [Relevox](https://github.com/tunahanaliozturk/relevox), the source-generated mediator with
MediatR 12's handler signatures. Reference it from test projects only.

```bash
dotnet add package Relevox.Testing
```

## A fake mediator for unit tests

`FakeMediator` implements `IMediator`. It answers what the test set up and records what was sent and published.
A request nobody set up fails with its type name instead of answering `default`.

```csharp
var mediator = new FakeMediator()
    .Setup((GetPrice query) => query.Sku == "bolt" ? 1.25m : 0m)
    .Setup((ArchiveOrder command) => { })
    .Throws<ChargeCard>(new TimeoutException("payments are down"));

await new CheckoutService(mediator).Complete(order);

mediator.Sent<ArchiveOrder>().ShouldHaveSingleItem();
mediator.Published<OrderCompleted>().ShouldHaveSingleItem();
```

## Recording notifications from the real mediator

```csharp
services.AddRelevox();
services.AddRelevoxNotificationRecorder();   // after AddRelevox

var recorder = provider.GetRequiredService<NotificationRecorder>();
recorder.Published<OrderCompleted>().ShouldHaveSingleItem();
```

The application's handlers still run, through the publisher it chose.

More in [docs/testing.md](https://github.com/tunahanaliozturk/relevox/blob/main/docs/testing.md).
