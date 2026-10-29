# Moving from MediatR 12 to Relevox

Relevox keeps MediatR 12's handler signatures, so a migration changes package references, `using` directives
and one registration call. Handlers, behaviours, processors, exception handlers and the code that sends
requests stay as they are.

[`samples/Migration`](../samples/Migration) is a worked example: the same order service on MediatR 12.5
(`Before`) and on Relevox (`After`). CI runs both and fails if what they print differs. Comparing the two
folders shows every change this guide describes:

```bash
diff -ru --exclude=bin --exclude=obj samples/Migration/Before samples/Migration/After
```

## Before you start

- **.NET 10.** Relevox targets `net10.0` only ([ADR 0003](adr/0003-net10-only.md)).
- **MediatR 12.x.** MediatR 13 and 14 have the same request and handler types as 12.5, so this guide applies
  to them too; also delete the `LicenseKey` line from the registration in step 3.
- **Know where your handlers are.** Relevox finds them while the application builds: in the application
  itself and in every project it references, directly or not. Handlers in an assembly loaded at run time,
  such as a plugin, are not found. See [Not supported](compatibility.md#not-supported).

## 1. Packages

The project that calls `AddMediatR`, usually the host, references `Relevox`, which brings the source generator.
Every other project that declares requests or handlers references `Relevox.Contracts`, which has no
dependencies.

```diff
 <!-- the host -->
-<PackageReference Include="MediatR" Version="12.5.0" />
+<PackageReference Include="Relevox" Version="..." />

 <!-- a library with requests and handlers -->
-<PackageReference Include="MediatR" Version="12.5.0" />
+<PackageReference Include="Relevox.Contracts" Version="..." />
```

Remove `MediatR.Contracts` and `MediatR.Extensions.Microsoft.DependencyInjection` too, if they are referenced.

Getting this split wrong is reported: a library that references `Relevox` instead of `Relevox.Contracts` gets a
mediator of its own, and the host's build warns with [RVX0005](diagnostics.md#rvx0005) when the library has
public handlers. If that library also gives the host `InternalsVisibleTo`, the host's `AddRelevox()` call can
bind to the library's method, and the build warns with [RVX0006](diagnostics.md#rvx0006).

### In Visual Studio, Rider or VS Code

Steps 2 and 3 are a code fix. Once the packages are swapped, every piece of MediatR code is marked with
[RVX0007](diagnostics.md#rvx0007), a suggestion that offers **Move from MediatR to Relevox**. Choose *Fix all
occurrences in: Solution* on any of them and the IDE rewrites the `using` directives, names rooted at
`MediatR`, `MediatRServiceConfiguration` and the registration calls in one go, dropping the scanning, licence
and generic-closing settings that steps 2 and 3 describe. What it leaves, such as `using
MediatR.Registration;` or an options object passed to `AddMediatR`, stays a compiler error for you to decide
about; the RVX0007 page lists those cases.

### From the command line

The same fix runs without an IDE, after the package swap and a restore:

```bash
dotnet format analyzers YourSolution.slnx --diagnostics RVX0007 --severity info
```

Run it from the solution's directory: `dotnet format` only changes files below the directory it runs in. A
`dotnet build` before the fix shows the compiler's errors for the missing `MediatR` namespace and not RVX0007,
because the compiler does not run analyzers once there are errors; the IDE and `dotnet format` do.

That is how a Clean Architecture solution and Microsoft's eShop were migrated, with their tests passing before
and after; see [migration-trial.md](migration-trial.md). The rest of this guide is what the fix does, for reviewing its result
and for doing it by hand.

## 2. `using` directives

| MediatR | Relevox |
|---|---|
| `using MediatR;` | `using Relevox;` |
| `using MediatR.Pipeline;` | `using Relevox.Pipeline;` |
| `using MediatR.NotificationPublishers;` | `using Relevox.NotificationPublishers;` |
| `using MediatR.Entities;` | `using Relevox.Entities;` |

The type names inside them are the same. A find and replace over the solution does it, with GNU `grep` and
`sed` (Linux, or Git Bash on Windows):

```bash
pattern='using MediatR(\.Pipeline|\.NotificationPublishers|\.Entities)?;'
grep -rlZE --include=*.cs "$pattern" . | xargs -0 -r sed -i -E "s/$pattern/using Relevox\1;/"
```

It rewrites only the four namespaces in the table, `global using` lines included. Any other `MediatR`
namespace left behind, such as `MediatR.Registration`, has no Relevox counterpart and shows up as a compile
error. Check `<Using Include="MediatR" />` items in project files as well.

## 3. Registration

`AddMediatR` becomes `AddRelevox`. It is declared in `Microsoft.Extensions.DependencyInjection`, as
`AddMediatR` is, so no new `using` is needed ([ADR 0007](adr/0007-addrelevox-namespace.md)).

```diff
-services.AddMediatR(options =>
+services.AddRelevox(options =>
 {
-    options.RegisterServicesFromAssemblyContaining<PlaceOrder>();
     options.AddOpenBehavior(typeof(Validation<,>));
     options.AddOpenRequestPreProcessor(typeof(Stamp<>));
 });
```

Delete the scanning calls, because there is nothing to scan: `RegisterServicesFromAssembly`,
`RegisterServicesFromAssemblies`, `RegisterServicesFromAssemblyContaining`, `AssembliesToRegister` and
`TypeEvaluator`. Delete `MediatorImplementationType` too: the mediator is generated, so there is no class to
swap. Everything else on MediatR's configuration exists on `RelevoxOptions` with the same name and number of
parameters: behaviours, stream behaviours, processors, `Lifetime`, `NotificationPublisher`, `NotificationPublisherType`,
`AutoRegisterRequestProcessors` and `RequestExceptionActionProcessorStrategy`. A test compares the two types
member by member. Delete `RegisterGenericHandlers` and the limits that go with it
(`MaxGenericTypeParameters`, `MaxGenericTypeRegistrations`, `MaxTypesClosing`, `RegistrationTimeout`): generic
handlers are closed while the project builds, whatever they said ([ADR 0008](adr/0008-generic-handlers.md)).
A generic request handler that MediatR's default never registered is live after the migration, and two of
them for the same request fail the build with [RVX0002](diagnostics.md#rvx0002).

If you named the configuration type, `MediatRServiceConfiguration` is `RelevoxOptions`.

### Test projects

A test project that only resolves the application's services needs nothing: it does not reference `Relevox`,
so it has no `AddRelevox` of its own, and if the application gives it `InternalsVisibleTo` it can call the
application's.

A test project that references `Relevox` itself, to declare test handlers, gets its own `AddRelevox` in its own
namespace when the application's is visible to it ([ADR 0007](adr/0007-addrelevox-namespace.md)). Code inside
the test project's namespace reaches its own; anywhere else a call can reach the application's, and the build
warns with [RVX0006](diagnostics.md#rvx0006). Calling it by its full name always works:

```csharp
global::Shop.Tests.RelevoxServiceCollectionExtensions.AddRelevox(services);
```

## 4. Build, and read what it reports

The first build tells you what MediatR would only have told you at run time:

- [RVX0001](diagnostics.md#rvx0001): a request with no handler. MediatR would throw when it was sent.
- [RVX0002](diagnostics.md#rvx0002): a request with two handlers. MediatR silently used one of them.
- [RVX0003](diagnostics.md#rvx0003): a handler the application cannot see, usually `internal` in a library.
  MediatR's scan registered it through reflection.
- [RVX0004](diagnostics.md#rvx0004): a generic handler the build cannot close, usually one whose type parameter
  has no constraint.
- [RVX0005](diagnostics.md#rvx0005): a library that references `Relevox` instead of `Relevox.Contracts`.
- [RVX0006](diagnostics.md#rvx0006): an `AddRelevox` call that registers another project's mediator.

Each one links to what it means and how to fix it.

## 5. Run your tests

Behaviour is held to MediatR 12.5's by tests that run the same scenarios on both libraries, including the
odd parts: which processors run, the order handlers run in, when a send throws instead of faulting its task.
[compatibility.md](compatibility.md) lists them. The differences that are deliberate:

- **Two handlers for one request is a build error** instead of a coin toss.
- **Behaviours do not run for a request nothing handles.** The send fails with the same exception type,
  without the behaviours' side effects. The build already warned with RVX0001.
- **Exception handlers are found for exception types the build can see**: those some handler for the request
  names, and `Exception`. One registered directly with the container for some other exception type is not
  found.
- **Exception messages differ.** Only exception types are part of the contract.
- **`new Mediator(serviceProvider)` does not exist.** The mediator is generated; resolve `IMediator`,
  `ISender` or `IPublisher` from the container.

## One feature at a time

The steps above move a whole solution in one change. In a large solution that is one pull request nobody
wants to review. `Relevox.MediatRBridge` lets MediatR and Relevox run side by side instead, so a team can move
a feature folder at a time and every step builds and passes its tests
([ADR 0022](adr/0022-mediatr-bridge.md)).

```bash
dotnet add package Relevox
dotnet add package Relevox.MediatRBridge
```

```csharp
services.AddMediatR(options => options.RegisterServicesFromAssemblyContaining<Program>()); // stays until the end
services.AddRelevox();
services.AddMediatRBridge();
```

The bridge takes MediatR's `IMediator`, `ISender` and `IPublisher`. Code that injects them does not change. A
message whose type implements Relevox's interface goes to Relevox; anything else goes to MediatR, as before.

To move a feature:

1. **Give its messages Relevox's interface as well as MediatR's**, so callers on MediatR's `ISender` still
   compile: `record PlaceOrder : MediatR.IRequest<int>, Relevox.IRequest<int>`.
2. **Move its handlers to Relevox's interfaces.** The build reports RVX0001 for a request that has Relevox's
   interface and no Relevox handler, so a half-moved feature shows up as a warning, or as an error with
   warnings treated as errors.
3. **Move the behaviours its requests need before the requests.** A request runs the pipeline of the library
   that handles it, and only that one. A behaviour that implements both libraries' interfaces, registered with
   both, runs once per request whichever side handles it. RVX0015 points at any that would be skipped in the
   project that moved or a project above it. It cannot see the move from a project below, such as a shared
   library of behaviours, so check those by hand; RVX0014 at the bridge call still counts them.
4. **Build and run the tests.**

Since the two libraries' delegates are different types, a behaviour serving both keeps its body in one place
and forwards to it:

```csharp
public class Logging<TRequest, TResponse>
    : MediatR.IPipelineBehavior<TRequest, TResponse>, Relevox.IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public Task<TResponse> Handle(TRequest request, MediatR.RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken) =>
        Run(request, () => next(), cancellationToken);

    public Task<TResponse> Handle(TRequest request, Relevox.RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken) =>
        Run(request, () => next(), cancellationToken);

    private async Task<TResponse> Run(TRequest request, Func<Task<TResponse>> next, CancellationToken cancellationToken)
    {
        // the original body
    }
}
```

Pre and post processors, exception handlers and actions have the same method in both libraries, so adding
Relevox's interface is enough.

A notification can have handlers on both sides while they move: MediatR's run first, then Relevox's.
[RVX0014](diagnostics.md#rvx0014), at the `AddMediatRBridge` call, counts what is left. Once
[RVX0016](diagnostics.md#rvx0016) says nothing is, delete MediatR's half of every type that has both, and the
MediatR overloads of the behaviours, then remove `AddMediatRBridge`, `AddMediatR` and the MediatR package, and
finish with the steps above. The order matters: the RVX0007 fix renames MediatR's interfaces to Relevox's, and
on a type that still has both it would leave the interface twice.
[migration-trial.md](migration-trial.md#clean-architecture-one-feature-at-a-time) has a solution moved this
way in six steps.

Code that injects Relevox's `ISender` cannot send a message that is still on MediatR, so move callers after the
messages they send.

## After the migration

Nothing below is needed to migrate. Each is a choice MediatR did not offer.

- **Inject the generated mediator where the call site knows what it sends.** `RelevoxMediator`, in the
  host's own namespace, has a typed `Send` per request that calls the handler with nothing to look up. It
  costs a quarter of MediatR's time, against about half through `ISender`
  ([measurements](benchmark-results/2026-10-17-m4.md)).
- **Make stateless handlers singletons** with `options.HandlerLifetime = ServiceLifetime.Singleton`, or by
  registering a handler yourself before `AddRelevox`. The generated mediator then resolves each handler once
  and keeps it.
- **Publish with NativeAOT.** Dispatch uses no reflection, and no trimming annotations are needed. The one
  exception is a generic notification handler reaching a notification type the build never saw, which is closed
  with `MakeGenericType` once per type; under NativeAOT that works for classes, not structs
  ([ADR 0009](adr/0009-open-notification-handlers-at-run-time.md)).
