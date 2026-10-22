# 15. What the message map says, and how the build learns about behaviours

Status: accepted
Date: 2026-10-22

## Context

ADR 0014 settled how the map leaves the build. This decides what is in it: for each request its handler, its
response, the behaviours, processors and exception handlers around it in the order they run, and the project it
lives in; for each notification its handlers in order; streams like requests.

Handlers, processors and exception handlers are already in the generator's plan. Behaviours are not. They are
registered when the application starts, through `AddRelevox`'s options or with the container, and the generated
mediator resolves them from the container on every send. The build has to learn about them some other way, or
the map leaves out the part of the pipeline people most often want to see.

## Decision

**Behaviours are read from the `AddRelevox` options lambda.** The generator already finds every `AddRelevox`
call. It now also walks the lambda passed to it for the registration methods of `RelevoxOptions`
(`AddBehavior`, `AddOpenBehavior`, `AddOpenBehaviors`, the stream behaviour and processor equivalents) and
for `AutoRegisterRequestProcessors = true`. Types are taken from `typeof(...)` and from generic type arguments,
which bind even though the lambda's parameter has no type until the generator has written `AddRelevox`.
Registrations run in source order of their method names, which is also the order of a chain such as
`options.AddOpenBehavior(a).AddOpenBehavior(b)`.

Anything the build cannot read, such as a `Type` in a variable or a method group passed instead of a lambda, is
listed in the map's `unread` field as written. The map says what it does not know rather than guessing.

**Open behaviours are checked against each request**, as the container checks them: it closes an open
registration over the service's type arguments in order and leaves it out when a constraint fails. The map uses
the same constraint check the generator uses for generic handlers, so `Transaction<TRequest, TResponse> where
TRequest : ICommand` appears on commands only.

**Processors follow the run-time rules.** Pre-processors appear only when the options add at least one, since
only then does the pre-processor behaviour run; processors found in the code are included when the options turn
on `AutoRegisterRequestProcessors`. Exception handlers and actions appear as the generated registration
registers them, open generic ones closed where their constraints allow.

**The map step binds names against the compilation.** The plan holds names, not symbols (see Models.cs), so the
step that writes the map combines the plan with the compilation and binds the names it needs: for a project, and
for checking an open behaviour's constraints. Plain namespace types are found by metadata name; generic, nested
and keyword types by speculative binding. The step runs on every edit when the switch is on; its output, the
JSON, only changes when the map does, so nothing downstream reruns and the file is not rewritten.

**The shape leaves room to grow.** Every handler, behaviour and processor is an object with `type` and
`project` rather than a bare string, so later releases can add fields, such as the call sites #46 records,
without breaking readers. Within 1.x fields are only added; `docs/message-map.md` says so and the schema in
`docs/message-map.schema.json` is checked against generated maps in a test.

Edit cost, from `EditCostMeasurement` with the switch on and an open behaviour in the options (median of the last
twenty of sixty edits, this machine, three runs): the map step takes 4-12 ms per edit at 500 handlers and
38-58 ms at 2,500, on top of discovery's 15-30 ms and 90-210 ms. With the switch off it takes no measurable time.
The machine was under memory pressure during these runs and the spread is wide; the order of magnitude is the
point.

## Consequences

- Behaviours registered with the container directly do not appear. `docs/message-map.md` says so and suggests
  registering them through the options. Reading `services.AddTransient(typeof(IPipelineBehavior<,>), ...)` too
  was considered and left out: container registrations can come from anywhere, including other libraries, and a
  map that found some of them would look complete when it was not.
- A registration inside an `if` in the lambda is in the map whether or not the branch runs.
- The registration's type is read from the lambda's syntax tree. If a behaviour class elsewhere changes the
  interfaces it implements, the IDE's cached reading of the lambda is stale until that file is edited. A real
  build compiles from scratch and is always right; the IDE never writes the file.
- The map is per project. An `AddRelevox` call in a different project from the one whose mediator it configures
  does not reach that project's map.

## Alternatives considered

- **Every `IPipelineBehavior` class in the compilation.** Simple, and wrong: a class that exists is not a class
  that is registered, and the order would be invented.
- **A runtime dump** (build the container, resolve each pipeline, write the result). Exact, but it needs the
  application to start, with its configuration and databases, in CI, and it is no longer a build artefact.
- **A behaviour attribute** (`[PipelineOrder(1)]`) the generator reads. A new API that duplicates what the
  options already say, and MediatR code moving to Relevox would not have it.
