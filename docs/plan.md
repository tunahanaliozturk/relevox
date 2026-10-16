# Çapar: plan

Name: **Çapar**, written `Capar` in package IDs and namespaces. A çapar was a mounted courier who
carried orders and news across the Ottoman roads at a gallop; the word comes from the Turkish verb
*çapmak*, to ride hard. A courier that is fast is the whole pitch of this library, so the name stuck.

Status: plan, before any code. The repository stays private until v1.0.0.

## 1. Why this exists

MediatR moved to a dual licence in 2025. Versions 13 and later ship under the Reciprocal Public
License 1.5 or a paid commercial licence from Lucky Penny Software, and the package validates a licence
key at runtime. 12.5.0 is the last Apache-2.0 release and it will not get new features or fixes.

A large number of .NET codebases use MediatR for in-process request/response, notifications and
pipeline behaviours. They need somewhere to go that is:

1. permissively licensed (MIT), with no key and no telemetry,
2. close enough to the MediatR 12 API that migration is a mechanical change,
3. measurably faster, and
4. safe for trimming and NativeAOT, which MediatR's reflection-based dispatch is not.

## 2. What we found before writing anything

### 2.1 MediatR 12.5.0 against 14.2.0, inspected with REA

Both assemblies were inspected with REA's managed-code tooling
(`rea inspect-managed-members`, metadata and CIL read without loading or running the assembly).

| | 12.5.0 (`lib/net6.0`) | 14.2.0 (`lib/net10.0`) |
|---|---|---|
| Licence | Apache-2.0 | RPL-1.5 or commercial |
| Public types | 38 | 38 |
| Evidence ID | `ev_faa355c2…6596af` | `ev_d95c1daa…25fda5a1` |

Observed differences in the public surface:

- `Microsoft.Extensions.DependencyInjection.ServiceCollectionExtensions` was renamed to
  `MediatRServiceCollectionExtensions`. Nothing else in the public type list changed.

Observed additions in internal types: a `MediatR.Licensing` namespace (`License`, `LicenseAccessor`,
`LicenseValidator`, `Edition`, `ProductType`, `BuildInfo`). The 14.x package also gained a dependency on
`Microsoft.IdentityModel.JsonWebTokens`, which is consistent with the licence key being a signed JWT
(inference from the dependency and type names; the validation path was not traced).

Conclusion: **the paid line added licensing, not API.** The compatibility target is the 12.5.0 public
surface, and that surface is what existing code is written against.

### 2.2 Clean-room rule

- No code is copied or translated from MediatR 13+ (RPL-1.5). Its binary was read for public type names
  only, to confirm the API had not moved.
- MediatR 12.5.0 is Apache-2.0. We read its documentation and observable behaviour, and use the package
  itself as a test oracle (section 6). We do not port its source. Çapar is written from the behaviour
  spec in this document.
- The interface shapes (`IRequest<T>`, `IRequestHandler<,>.Handle(request, ct)`, and so on) are kept
  because matching them is the whole point of a migration path. API shape is not code.

### 2.3 The other open-source option

`Mediator` by Martin Othamar (MIT, source-generated, 3.1 in release candidate) already exists and is
fast. Çapar has to justify itself against it, not only against MediatR:

| | MediatR 12.5 | Mediator 3.x | Çapar target |
|---|---|---|---|
| Licence | Apache-2.0, frozen | MIT | MIT |
| Dispatch | reflection + dictionary | source-generated | source-generated |
| Handler return type | `Task<T>` | `ValueTask<T>` | `Task<T>` (see ADR 0002) |
| Migration from MediatR | n/a | rewrite signatures | change `using`, keep signatures |
| Missing handler | runtime exception | compile-time diagnostic | compile-time diagnostic |
| NativeAOT / trimming | no | yes | yes, proved in CI |
| Pre/post processors, exception handlers | yes | partly | yes, same ordering as MediatR |

The niche is the third row: **swap the package and the namespace, keep every handler, get generated
dispatch.** If Çapar is not faster than Mediator, we say so in the README and the migration story
still stands on its own. If a benchmark shows our `Task` decision costs more than it is worth, that is
a finding and ADR 0002 gets revisited.

## 3. Scope of v1.0.0

### In

Feature parity with the MediatR 12.5.0 public surface:

- Requests: `IRequest`, `IRequest<TResponse>`, `IRequestHandler<TRequest>`,
  `IRequestHandler<TRequest, TResponse>`, `IBaseRequest`.
- Notifications: `INotification`, `INotificationHandler<T>`, `INotificationPublisher` with
  sequential (`ForeachAwaitPublisher`) and parallel (`TaskWhenAllPublisher`) strategies.
- Streams: `IStreamRequest<T>`, `IStreamRequestHandler<,>`, `IStreamPipelineBehavior<,>`.
- `ISender`, `IPublisher`, `IMediator`, including the `object` overloads (`Send(object)`,
  `Publish(object)`, `CreateStream(object)`).
- Pipeline: `IPipelineBehavior<,>`, open generic behaviours, `IRequestPreProcessor<>`,
  `IRequestPostProcessor<,>`, `IRequestExceptionHandler<,,>`, `IRequestExceptionAction<,>`, with the
  same ordering and exception semantics as MediatR 12.5.0.
- Registration: `services.AddCapar(cfg => ...)` with the familiar knobs (lifetime, publisher strategy,
  behaviours, open behaviours). Handler discovery is done by the generator at compile time, including
  handlers in referenced projects.
- Compile-time diagnostics: no handler, more than one handler for a request, handler type not
  accessible to generated code, unsupported generic handler shapes.
- Trimming and NativeAOT with zero warnings.

### Out (for v1)

- Generic request handlers resolved by constraint at runtime (MediatR 12.3+ feature). The generator can
  only emit closed handlers it can see. Listed as a known limitation with a diagnostic that says so.
- A `namespace MediatR` drop-in shim. It would make migration a one-line change but would put another
  project's name on our types and break when both packages are referenced. A code fix is the better tool
  and is planned for after v1.
- Built-in OpenTelemetry, validation, or logging behaviours. Users write a ten-line behaviour; we ship a
  sample, not a package.
- `netstandard2.0` and .NET Framework. .NET 8 and 9 both leave support in November 2026, so v1 targets
  `net10.0` only (ADR 0003).

## 4. Design

### 4.1 Packages

| Package | Contents | Dependencies |
|---|---|---|
| `Capar.Contracts` | marker and handler interfaces only, for domain projects | none |
| `Capar` | runtime, DI extension, source generator and analyzers in `analyzers/dotnet/cs` | `Capar.Contracts`, `Microsoft.Extensions.DependencyInjection.Abstractions` |

One install line for the user (`dotnet add package Capar`). The generator ships inside the runtime
package, so the two can never be out of step.

### 4.2 How a `Send` works

The generator emits, per compilation that calls `AddCapar`:

1. A sealed `Mediator` implementation with one strongly typed `Send` overload per discovered request
   type. A call site that knows the static type binds to that overload directly: no lookup at all.
2. A `Send<TResponse>(IRequest<TResponse>)` fallback that switches on the runtime type with a generated
   `switch` over sealed type checks, for callers holding the interface.
3. `Send(object)` with the same switch, returning `Task<object?>`.
4. Per request, a pipeline method. With no behaviours registered for that request it calls the handler
   directly: no delegate, no closure, no array. With behaviours, it builds the chain once per call from
   the behaviours resolved out of the current scope, so scoped behaviours keep their meaning.
5. A registration method that adds every handler, processor and behaviour to the `IServiceCollection`.
   No assembly scanning at startup.

Handlers are resolved from `IServiceProvider` on each call, as in MediatR, so lifetimes behave the same.
Caching a handler instance would be faster and would also be a bug for every scoped handler.

### 4.3 Publish

Generated `Publish` overloads resolve `IEnumerable<INotificationHandler<T>>` and hand them to the
configured `INotificationPublisher`. The default stays sequential, in registration order, stopping on
the first exception, matching MediatR. The parallel publisher is opt-in.

### 4.4 Cross-project discovery

Handlers often live in an `Application` project while `AddCapar` is called in the host. The generator
in the host walks referenced assemblies' symbols (`Compilation.References`) for types implementing the
handler interfaces, limited to assemblies that reference `Capar.Contracts`, so it does not walk the
whole BCL. Handlers must be `public` or `internal` with `InternalsVisibleTo`; anything else gets a
diagnostic, not a silent skip.

### 4.5 Diagnostics

| ID | Severity | When |
|---|---|---|
| CPR0001 | Error | a request type sent somewhere has no handler |
| CPR0002 | Error | two handlers for the same request type |
| CPR0003 | Error | handler not accessible from the generated code |
| CPR0004 | Warning | open generic handler that the generator cannot close |
| CPR0005 | Warning | `Send(object)` or `Publish(object)` used where the static type is known |

CPR0001 only fires for request types the generator can see being sent. A request built by reflection
still fails at runtime, with an exception that names the type.

## 5. Performance plan

Rule: no number in the README that a committed benchmark did not produce, with hardware named.

Baselines are measured **first**, in M0, before Çapar has any code: MediatR 12.5.0 and Mediator 3.x on
the same scenarios. These are the numbers we have to beat.

| Scenario | What it shows |
|---|---|
| `Send`, no behaviours, handler completes synchronously | raw dispatch cost |
| `Send` via `IRequest<T>` and via `object` | cost of the fallback paths |
| `Send` with 3 behaviours + pre/post processors | pipeline cost |
| `Publish` to 1 and to 5 handlers, both publishers | notification fan-out |
| `CreateStream`, 100 items | stream overhead per item |
| Cold start: build provider and send the first request | startup, where assembly scanning hurts |
| NativeAOT publish size and first-request time | AOT story |

Hypotheses to confirm or disprove (not claims):

- `Send` with no behaviours allocates nothing beyond what the handler itself returns.
- `Send` is at least 5x faster than MediatR 12.5.0.
- `Send` is within 10% of Mediator 3.x despite returning `Task`.
- Cold start is faster than MediatR's because nothing is scanned.

Results go under `docs/benchmark-results/`. A hypothesis the numbers reject is reported as rejected.

## 6. Testing

- **Unit tests** (xUnit v3, Shouldly): runtime behaviour, publisher strategies, pipeline ordering,
  exception handler state.
- **Generator tests**: input source, expected diagnostics, and a compile-and-run check of generated
  output. Snapshot tests are avoided; they assert formatting rather than behaviour.
- **Differential tests against MediatR 12.5.0.** The same scenario runs on both libraries and both write
  to a trace (which handler, behaviour, processor ran, in what order, with what exception). The traces
  must be equal. MediatR 12.5.0 is Apache-2.0, so it is allowed as a test-only dependency. This is the
  test that keeps "same semantics" from being a claim.
- **NativeAOT test**: a sample app is published with `PublishAot=true` in CI and run; the job fails on
  any trim or AOT warning and on a wrong answer.
- **Migration test**: a small but realistic MediatR sample (commands, queries, notifications, a
  validation behaviour, an exception handler) migrated by the documented steps; its tests pass before
  and after.

## 7. Repository layout

```
capar/
├── src/Capar.Contracts/
├── src/Capar/
├── src/Capar.SourceGenerator/        # packed into Capar, not shipped alone
├── tests/Capar.UnitTests/
├── tests/Capar.SourceGenerator.Tests/
├── tests/Capar.DifferentialTests/    # MediatR 12.5.0 as oracle
├── samples/Capar.Samples.Aot/
├── samples/Capar.Samples.Migration/
├── benchmarks/Capar.Benchmarks/
├── tools/Capar.LicenseAudit/         # copied forward from an existing repo
├── docs/adr/  docs/benchmark-results/  docs/migrating-from-mediatr.md
└── the usual root files (house standard)
```

The generator setup (packing, `IIncrementalGenerator`, test harness) is copied forward from `fastcast`.

## 8. Milestones

Each milestone is a branch and a PR with green CI. Commits land as each unit passes its gates.

| | Deliverable | Done when |
|---|---|---|
| M0 | Scaffold, quality gates, CI, licence audit, **baseline benchmarks of MediatR 12.5.0 and Mediator 3.x** | baseline numbers committed |
| M1 | Contracts, generator discovers handlers (same and referenced projects), `Send`/`Publish`/`CreateStream`, `AddCapar` | differential tests pass for request, notification, stream |
| M2 | Behaviours, open behaviours, pre/post processors, exception handlers and actions, stream behaviours, publishers | differential pipeline-order tests pass |
| M3 | Diagnostics CPR0001-CPR0005, NativeAOT sample in CI | AOT job green with zero warnings |
| M4 | Performance pass against the M0 baselines | every hypothesis in section 5 marked confirmed or rejected |
| M5 | Migration guide and migrated sample, README, ADRs, operations notes, CHANGELOG | a reader can migrate the sample from the guide alone |
| M6 | v1.0.0: NuGet publish, repository made public | only with the owner's go-ahead |

## 9. ADRs to write

- 0001 Source-generated dispatch, not reflection or expression trees.
- 0002 Handlers return `Task<T>`, not `ValueTask<T>`, to keep MediatR signatures. Revisit if M4 shows the
  cost matters.
- 0003 `net10.0` only.
- 0004 Handlers resolved per call from the container; no handler caching.
- 0005 No `namespace MediatR` shim; a code fix later instead.
- 0006 Clean-room rule and the use of MediatR 12.5.0 as a test oracle.

## 10. Open questions

- Logo: decided. The "Nal-Ç" mark (a horseshoe drawn as Ç; nail holes are the pipeline stops, the dot
  is the message) in night `#0F172A` and cobalt `#3B82F6`. Sources in `docs/brand/`. The 128 px PNG
  that NuGet requires is rendered from the SVG at M5.
- NuGet ID prefix reservation, which needs the package owner account; only relevant at M6.
