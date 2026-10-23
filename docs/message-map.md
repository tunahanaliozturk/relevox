# The message map

The message map is a file that says what a project's mediator dispatches and what runs around each message.
The generator already knows this at build time; the map writes it down, so it can be read in a pull request or
checked in CI instead of found with grep.

How the map leaves the build is in [ADR 0014](adr/0014-message-map-output.md); what goes into it, and what the
build cannot see, is in [ADR 0015](adr/0015-message-map-content.md).

## Turning it on

In the project that calls `AddRelevox`, or for every project in `Directory.Build.props`:

```xml
<PropertyGroup>
  <RelevoxMessageMap>true</RelevoxMessageMap>
  <!-- Optional. Relative to the project; the default is under obj. -->
  <RelevoxMessageMapPath>message-map.json</RelevoxMessageMapPath>
</PropertyGroup>
```

Every build that compiles the project then writes the map, by default to
`obj/<configuration>/<framework>/relevox-message-map.json`. The file is only rewritten when its content
changes, so an edit that changes no message, handler or registration leaves it alone. Projects that use another
project's mediator have no map of their own and get no file.

The map is written by a real build: `dotnet build`, or a build in Visual Studio or Rider. The IDE's background
design-time builds do not write it.

With the switch on, the map is also compiled into the assembly as the constant `RelevoxMessageMap.Json`, which
is where the build task reads it from. With it off, the generated code is the same as without this feature.

## An example

```json
{
  "schemaVersion": 1,
  "project": "Shop.Application",
  "requests": [
    {
      "type": "Shop.Orders.PlaceOrder",
      "response": "int",
      "project": "Shop.Contracts",
      "handler": {
        "type": "Shop.Orders.PlaceOrderHandler",
        "project": "Shop.Application"
      },
      "behaviors": [
        {
          "type": "Shop.Logging<Shop.Orders.PlaceOrder, int>",
          "project": "Shop.Application"
        },
        {
          "type": "Shop.Transaction<Shop.Orders.PlaceOrder, int>",
          "project": "Shop.Application"
        }
      ],
      "preProcessors": [],
      "postProcessors": [],
      "exceptionActions": [],
      "exceptionHandlers": [
        {
          "type": "Shop.Orders.OutOfStock",
          "project": "Shop.Application",
          "exception": "Shop.Orders.OutOfStockException"
        }
      ]
    }
  ],
  "streams": [],
  "notifications": [
    {
      "type": "Shop.Orders.OrderPlaced",
      "project": "Shop.Contracts",
      "handlers": [
        {
          "type": "Shop.Billing.SendInvoice",
          "project": "Shop.Application"
        }
      ]
    }
  ],
  "unread": []
}
```

## The fields

The JSON schema is [message-map.schema.json](message-map.schema.json); a test checks generated maps against it.

Types are written as C# names with their namespaces and without `global::`: `Shop.Orders.PlaceOrder`,
`Shop.Paged<Shop.Order>`, `string`, `int?`. A project is an assembly name. Lists of messages are sorted by
type name; everything inside a message is in the order it runs.

### Top level

| Field | Type | Meaning |
|---|---|---|
| `schemaVersion` | number | `1`. Changes only if a field is removed or changes meaning, which 1.x never does. |
| `project` | string | The project whose mediator this is. |
| `requests` | array | Every request the mediator dispatches, sorted by `type`. |
| `streams` | array | Every stream request, sorted by `type`. |
| `notifications` | array | Every notification type the build saw, sorted by `type`. |
| `unread` | array of strings | Registrations in an `AddRelevox` options lambda that the build could not read, as written, such as `options.AddOpenBehavior(behaviorType)`. Behaviours and processors registered there are missing from the map. Empty when everything was read. |

### A request

| Field | Type | Meaning |
|---|---|---|
| `type` | string | The request type. A generic request appears once for each closing the build found, such as `Shop.Load<Shop.Order>`. Abstract requests are left out: they can never be sent as themselves. |
| `response` | string or null | The response type; `null` for a request that returns nothing. |
| `project` | string or null | The project the request type is declared in. |
| `handler` | component | The handler that runs. |
| `behaviors` | components | Pipeline behaviours added through the options, outermost first. An open behaviour appears closed over this request, and only when its constraints allow it, as the container decides. |
| `preProcessors` | components | Pre-processors, in the order they run. Empty unless the options add at least one, because only then does the pre-processor behaviour run ([compatibility](compatibility.md)). Processors found in the code are included when the options set `AutoRegisterRequestProcessors = true`. |
| `postProcessors` | components | The same for post-processors. |
| `exceptionActions` | exception components | Exception actions registered for this request, in the order they are tried for one exception type. Which ones run depends on the exception thrown: those for its type, then for each base type up to `Exception`. |
| `exceptionHandlers` | exception components | The same for exception handlers. |

The pipeline, from the outside in: exception actions and exception handlers (actions outermost with the default
`ApplyForUnhandledExceptions` strategy, handlers outermost with `ApplyForAllExceptions`), then the
pre-processors, then the post-processors, then `behaviors`, then the handler.

### A stream

| Field | Type | Meaning |
|---|---|---|
| `type` | string | The stream request type. |
| `response` | string | The type of each item. |
| `project` | string or null | The project the type is declared in. |
| `handler` | component | The handler that runs. |
| `behaviors` | components | Stream behaviours added through the options, outermost first. |

### A notification

| Field | Type | Meaning |
|---|---|---|
| `type` | string | The notification type. Abstract notification types are left out. |
| `project` | string or null | The project the type is declared in. |
| `handlers` | components | The handlers it reaches, in the order they run, including handlers written for a base type or interface and generic handlers closed at build time. |

### A component

| Field | Type | Meaning |
|---|---|---|
| `type` | string | The class. |
| `project` | string or null | The project it is declared in; `null` if the build could not tell. |

An exception component adds `exception`, the exception type it is registered for.

## What the map cannot see

The map is what the build can know. Three things are only known when the application runs:

- **Behaviours and processors registered with the container directly**, such as
  `services.AddTransient(typeof(IPipelineBehavior<,>), typeof(Logging<,>))`. They run, but are not in the map.
  Registering them through the `AddRelevox` options instead puts them in it.
- **Options the build cannot read**: a `Type` held in a variable, a method passed to `AddRelevox` instead of a
  lambda, a call the options are handed to, such as `Configure(options)`, an extension method on them or
  `options.BehaviorsToRegister.Clear()`, a call while an extension method of the same name is in scope, which the call
  may pick instead, the options held in another variable, including the options a registration
  call returns, and a lambda or local function
  inside the options lambda that uses them, since it may run later or never. These are listed in `unread`.
  Another library's method that happens to be called `AddRelevox` is ignored.
- **Which of several `AddRelevox` calls runs first.** Only the first call to run applies its options; the next
  returns before calling its lambda. With more than one call in a project, the calls that pass options are
  listed in `unread` and their registrations are left out.
- **Branches in the options lambda.** A registration inside an `if` is in the map whether or not the branch is
  taken. A change to `AutoRegisterRequestProcessors` inside a branch or a loop is listed in `unread`.

## Compatibility

Within 1.x, fields are only added. A tool that reads the map should ignore fields it does not know. A field is
never removed or given a new meaning without a new `schemaVersion`, and that only happens in a major release.
