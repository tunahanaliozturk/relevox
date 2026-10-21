# Telemetry

With telemetry on, every send, publish and stream enumeration gets a span on the `ActivitySource` named
`Relevox` and a duration measurement on the `Meter` of the same name. The names and attributes are fixed for
the whole 1.x line by
[ADR 0013](adr/0013-telemetry-names.md), so dashboards and alerts built on them keep working across upgrades.

Telemetry is off by default. An application that never turns it on pays for one `bool` read per call.

## Turning it on

Set the switch in `AddRelevox`, then tell OpenTelemetry to listen to the source and the meter:

```csharp
builder.Services.AddRelevox(options => options.Telemetry.Enabled = true);

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddSource(RelevoxTelemetry.SourceName)
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddMeter(RelevoxTelemetry.SourceName)
        .AddOtlpExporter());
```

Tracing and metrics are independent. Add only the source, or only the meter, and you get only that one.

`RelevoxTelemetry.SourceName` is the string `"Relevox"`. Relevox itself does not depend on OpenTelemetry: it
uses `System.Diagnostics.ActivitySource` and `System.Diagnostics.Metrics.Meter`, which any listener can
subscribe to, OpenTelemetry's included.

With the switch on and nothing listening, the mediator asks `ActivitySource.HasListeners()` and whether the
histogram is enabled, and does nothing else. Spans are only built, and durations only measured, once something
subscribes.

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

## Metrics

| Instrument | Type | Unit | Attributes |
|---|---|---|---|
| `relevox.operation.duration` | histogram | `s` | `relevox.operation`, `relevox.message.type`, and `error.type` when the operation failed |

One measurement is recorded per span, with the same start and end: a send or a publish from the call to the
end of its task, a stream from the first `MoveNextAsync` to the end of that enumeration. The attribute values
are the same as on the span, so a notification type the build never saw is measured under its runtime name too.

The histogram suggests bucket boundaries in seconds, from 5 ms to 10 s, the ones OpenTelemetry recommends for
HTTP durations. An SDK view can replace them.

There is no separate error counter. A failed operation is a measurement with `error.type` set, so the count of
errors is the histogram's count filtered on that attribute. With the Prometheus exporter, which turns the dots
into underscores and adds the unit, errors per second for each message type over the last five minutes are:

```promql
sum by (relevox_message_type, error_type) (
  rate(relevox_operation_duration_seconds_count{error_type!=""}[5m])
)
```

and the 95th percentile duration of each request type:

```promql
histogram_quantile(0.95,
  sum by (relevox_message_type, le) (
    rate(relevox_operation_duration_seconds_bucket{relevox_operation="send"}[5m])
  )
)
```

## What is not covered

- Each notification handler on its own. A publish is one span and one measurement, with the handler count as
  an attribute on the span.
- Behaviours and processors on their own. Their time is inside the span and the measurement of the call they
  belong to.

## Under NativeAOT

Telemetry works the same in a NativeAOT binary and raises no trimming or AOT warnings. The
[AOT sample](../samples/Relevox.Samples.Aot) turns it on and checks the spans and measurements it gets.
