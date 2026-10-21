# 13. Telemetry names, and telemetry off by default

Status: accepted
Date: 2026-10-20

## Context

1.2 adds OpenTelemetry tracing and metrics to sends, publishes and streams. Span, attribute and instrument
names are public API in all but name: people build dashboards and alerts on them, and renaming one breaks those
without a compiler error anywhere. So the names are decided here, before any code.

OpenTelemetry's messaging conventions do not fit. They describe a message crossing a process boundary through a
broker: `messaging.system`, a destination, a producer span and a consumer span linked across the wire. A mediator
call is a method call inside one process, on one thread of work. Borrowing `messaging.*` would tell every backend
that recognises those attributes something untrue, such as that a request went through a queue.

The generator knows every request and handler type at build time, and every notification type the code names, so
whatever names it uses for those can be string constants in the generated code. One kind of publish is different:
a notification type that exists only at run time, such as `EntityCreated<T>` closed inside a generic base class,
reaches its handlers through `OpenNotificationHandlers` ([ADR 0009](0009-open-notification-handlers-at-run-time.md)),
and its name cannot be a constant.

## Decision

**Sources.** One `ActivitySource` and one `Meter`, both named `Relevox`, versioned with the package's
informational version. A public static class `RelevoxTelemetry` exposes the name as `RelevoxTelemetry.SourceName`
for `AddSource` and `AddMeter`.

**Spans.** One span per `Send`, `Publish` and `CreateStream` call, of kind `Internal`, named by operation and
message type: `send PlaceOrder`, `publish OrderPlaced`, `stream ListOrders`. The type in the name is the short
C# name, generic arguments included (`send Paged<Order>`), so the name stays low in cardinality and readable in
a trace view.

A stream gets one span, and one measurement, per enumeration, from the first `MoveNextAsync` to the end, a failure
or disposal, because that is when its handler runs. The stream `CreateStream` returns can be enumerated more than
once, and each enumeration resolves and runs the handler again, so each is its own operation. A stream that is
created and never enumerated emits nothing.

A publish of a notification type known only at run time takes its span name and `relevox.message.type` from the
notification's type when it is published, worked out once per type and cached. That is `Type.Name` and
`Type.FullName`, which trimming keeps, with generic arguments written the same way as the generated constants.

**Attributes.**

| Attribute | On | Value |
|---|---|---|
| `relevox.operation` | every span and measurement | `send`, `publish` or `stream` |
| `relevox.message.type` | every span and measurement | the message's full type name, `Shop.Orders.PlaceOrder` |
| `relevox.handler.type` | send and stream spans | the handler's full type name |
| `relevox.notification.handler_count` | publish spans | how many handlers the notification reaches |
| `error.type` | failed spans and measurements | the exception's full type name, as OpenTelemetry's general conventions define it |

A failed operation sets the span status to `Error`, records the exception as a span event with
`Activity.AddException`, and rethrows it unchanged.

**Metrics.** One instrument, the histogram `relevox.operation.duration`, in seconds (`s`), with
`relevox.operation`, `relevox.message.type` and, on failure, `error.type`. Errors are counted by filtering on
`error.type`, which is how OpenTelemetry's own HTTP and database conventions do it, rather than by a second
counter that would have to agree with the histogram.

**The switch.** Off by default. Turned on with `options.Telemetry.Enabled = true` in `AddRelevox`. The options
object, `RelevoxTelemetryOptions`, leaves room for what comes later in 1.2, such as a span per notification
handler, without another top-level property each time.

**What off costs.** With telemetry off, the generated mediator reads one `bool` field per call and does nothing
else. With it on and nothing listening, it asks `ActivitySource.HasListeners()` and `Histogram.Enabled` and
does nothing else. Both claims get a benchmark row in 1.2.

**Stability.** Within 1.x the names above do not change and no attribute is removed. Attributes and instruments
may be added. A value's format may not change: `relevox.message.type` stays the full type name.

## Consequences

- A trace shows `send PlaceOrder` under the ASP.NET Core request span that caused it, with the handler named,
  and nothing has to be configured beyond the switch and `AddSource("Relevox")`.
- Turning telemetry on is a decision the application makes once. An application that never does pays one field
  read per call.
- `error.type` on a histogram means an "errors per request type" panel is a query, not an instrument; backends
  that cannot filter histograms by attribute need a recording rule.
- Backends that special-case messaging spans will not treat these as messaging, which is accurate.

## Alternatives considered

- **OpenTelemetry's messaging conventions.** Recognised by many backends, and wrong about what happened: there is
  no broker, no destination and no second process. Rejected for that reason alone.
- **One `ActivitySource` per message type**, such as `Relevox.Shop.Orders.PlaceOrder`. Fine-grained filtering at
  the source, but `AddSource` would need wildcards and every new request type would be a new source to manage.
- **On by default.** Every application would pay for `HasListeners()` on every call, and one that already traces
  through its own behaviour would get two spans per request after upgrading, which 1.x promises not to do.
- **A separate `relevox.request.errors` counter**, as first planned in the 1.2 issues. Two instruments that must
  agree are one more thing to get wrong; `error.type` on the duration histogram gives the same numbers.
- **Span names with the full type name.** Unambiguous, and long enough to be truncated in most trace views. The
  full name is in `relevox.message.type` for anyone who needs it.
- **`AddRelevox(o => o.EnableTelemetry())`.** Reads well, but a method with options of its own would be a second
  way to configure the same thing once per-handler spans arrive.

## Addendum: a span per notification handler

Date: 2026-10-21

`options.Telemetry.NotificationHandlerSpans`, off by default and ignored while `Enabled` is off, gives every
notification handler a span of its own, as a child of the publish span. These names join the ones above and
come under the same stability promise.

- **Span name.** `handle OrderPlaced`: the operation and the notification's short name, the same rule as every
  other span. All the handlers of one publish share the name, and the handler is told apart by its attribute.
- **Attributes.** `relevox.operation` is `handle`, `relevox.message.type` is the notification's full type name,
  and `relevox.handler.type` is the handler's full type name, written as for send and stream spans. A failure
  adds `error.type`, sets the status to `Error` and records the exception, as on every other span.
- **No measurement.** Handler spans add nothing to `relevox.operation.duration`. The publish is already
  measured, and a measurement per handler under the same instrument would count its time twice.
- **Where the span starts.** Around the handler's own call, in the executor a publisher is given and in the
  generated loop that stands in for `ForeachAwaitPublisher`, so the built-in publishers and one an application
  writes are covered alike. The paths with handler spans are separate generated methods: with the option off,
  a publish does no extra work per handler.

Two alternatives were considered. Naming the span after the handler, `handle SendReceipt`, reads better in a
trace view, but it breaks the rule that a span is named by its operation and message type, and a query for
everything a notification caused would need the handler names. Recording a duration per handler would make a
slow handler a metric query rather than a trace search, but it needs either a second instrument or a
`relevox.operation` value that makes the existing histogram double count; it can be added later without
changing anything decided here.
