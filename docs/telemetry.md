# Telemetry

With telemetry on, every send, publish and stream enumeration gets a span on the `ActivitySource` named
`Relevox`. The names and attributes are fixed for the whole 1.x line by
[ADR 0013](adr/0013-telemetry-names.md), so dashboards and alerts built on them keep working across upgrades.

Telemetry is off by default. An application that never turns it on pays for one `bool` read per call.

## Turning it on

Set the switch in `AddRelevox`, then tell OpenTelemetry to listen to the source:

```csharp
builder.Services.AddRelevox(options => options.Telemetry.Enabled = true);

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddSource(RelevoxTelemetry.SourceName)
        .AddOtlpExporter());
```

`RelevoxTelemetry.SourceName` is the string `"Relevox"`. Relevox itself does not depend on OpenTelemetry: it
uses `System.Diagnostics.ActivitySource`, which any listener can subscribe to, OpenTelemetry's included.

With the switch on and nothing listening, the mediator asks `ActivitySource.HasListeners()` and does nothing
else. Spans are only built once something subscribes.

## What a trace looks like

A request that sends a command whose handler publishes a notification shows up like this:

```text
POST /orders                      (ASP.NET Core)
  send PlaceOrder                 (Relevox)
    publish OrderPlaced           (Relevox)
```

Each span is a child of whatever activity was current when the call was made, so a send from inside a handler
sits under that handler's span. After `Send` returns, the caller's current activity is the one it had before.

## Spans

| Call | Span name | Kind |
|---|---|---|
| `Send` | `send PlaceOrder` | `Internal` |
| `Publish` | `publish OrderPlaced` | `Internal` |
| `CreateStream` | `stream ListOrders` | `Internal` |

The type in the name is the short name, with generic arguments written out (`send Paged<Int32>`). The full
name is in an attribute.

Every way of sending is traced the same way: the typed overloads on `RelevoxMediator`, `ISender`, `IMediator`
and `Send(object)`. A request with no handler fails before a span starts.

### Attributes

| Attribute | On | Value |
|---|---|---|
| `relevox.operation` | every span | `send`, `publish` or `stream` |
| `relevox.message.type` | every span | the message's full type name, such as `Shop.Orders.PlaceOrder` |
| `relevox.handler.type` | send and stream spans | the handler's full type name |
| `relevox.notification.handler_count` | publish spans | how many handlers the notification reaches |
| `error.type` | failed spans | the exception's full type name, such as `System.TimeoutException` |

Type names are the runtime's names: `System.Int32` rather than `int`, `System.Nullable<System.Int32>` rather
than `int?`, and a dot between a nested type and the type around it. The generator writes them into the
generated mediator as string constants, so starting a span involves no reflection.

### Failures

When a handler, a behaviour or a processor throws, the span gets status `Error`, an `error.type` attribute and
an `exception` event recorded with `Activity.AddException`. The exception then reaches the caller exactly as it
would with telemetry off: the same exception object, and at the same moment. A handler that throws before
returning a task still throws out of `Send`, and one that faults its task still returns a faulted task.

### Streams

A stream's span covers one enumeration: it starts at the first `MoveNextAsync`, when the handler is resolved
and called, and ends when the stream finishes, fails, or is disposed early, for instance by a `break` out of
`await foreach`. The stream `CreateStream` returns can be enumerated more than once, and each enumeration runs
the handler again, so each gets a span of its own. A stream that is created and never enumerated emits nothing.

### Notifications the build never saw

A notification type built from a type parameter, such as `new EntityCreated<TSelf>()` in a generic base class,
reaches only generic handlers closed at run time ([ADR 0009](adr/0009-open-notification-handlers-at-run-time.md)).
Its publish is traced as well. The name is worked out from the type the first time it is published and then
cached, written the same way as the generated constants: `publish EntityCreated<Invoice>`, with
`relevox.message.type` set to `Shop.EntityCreated<Shop.Invoice>`.

## What is not traced

- Each notification handler on its own. A publish is one span, with the handler count as an attribute.
- Behaviours and processors on their own. Their time is inside the span of the call they belong to.

## Under NativeAOT

Telemetry works the same in a NativeAOT binary and raises no trimming or AOT warnings. The
[AOT sample](../samples/Relevox.Samples.Aot) turns it on and checks the spans it gets.
