# Moving from MediatR 12 to Çapar

Çapar keeps MediatR 12's handler signatures, so a migration changes package references, `using` directives
and one registration call. Handlers, behaviours, processors, exception handlers and the code that sends
requests stay as they are.

[`samples/Migration`](../samples/Migration) is a worked example: the same order service on MediatR 12.5
(`Before`) and on Çapar (`After`). CI runs both and fails if what they print differs. Comparing the two
folders shows every change this guide describes:

```bash
diff -ru --exclude=bin --exclude=obj samples/Migration/Before samples/Migration/After
```

## Before you start

- **.NET 10.** Çapar targets `net10.0` only ([ADR 0003](adr/0003-net10-only.md)).
- **MediatR 12.x.** MediatR 13 and 14 have the same request and handler types as 12.5, so this guide applies
  to them too; also delete the `LicenseKey` line from the registration in step 3.
- **Know where your handlers are.** Çapar finds them while the application builds: in the application
  itself and in every project it references, directly or not. Handlers in an assembly loaded at run time,
  such as a plugin, are not found. See [Not supported](compatibility.md#not-supported).

## 1. Packages

The project that calls `AddMediatR`, usually the host, references `Capar`, which brings the source generator.
Every other project that declares requests or handlers references `Capar.Contracts`, which has no
dependencies.

```diff
 <!-- the host -->
-<PackageReference Include="MediatR" Version="12.5.0" />
+<PackageReference Include="Capar" Version="..." />

 <!-- a library with requests and handlers -->
-<PackageReference Include="MediatR" Version="12.5.0" />
+<PackageReference Include="Capar.Contracts" Version="..." />
```

Remove `MediatR.Contracts` and `MediatR.Extensions.Microsoft.DependencyInjection` too, if they are referenced.

Getting this split wrong is reported: a library that references `Capar` instead of `Capar.Contracts` gets a
mediator of its own, and the host's build warns with [CPR0005](diagnostics.md#cpr0005) when the library has
public handlers. If that library also gives the host `InternalsVisibleTo`, the host's `AddCapar()` call can
bind to the library's method, and the build warns with [CPR0006](diagnostics.md#cpr0006).

### In Visual Studio, Rider or VS Code

Steps 2 and 3 are a code fix. Once the packages are swapped, every `using MediatR;` and every `AddMediatR`
call is a compiler error, and each one offers **Move from MediatR to Capar**. Choose *Fix all occurrences in:
Solution* on any of them and the IDE rewrites the `using` directives, names qualified with `MediatR.`,
`MediatRServiceConfiguration` and the registration calls in one go, dropping the scanning, licence and
generic-closing settings that steps 2 and 3 describe. Anything it leaves, such as `using MediatR.Registration;`,
has no Çapar counterpart and stays an error for you to remove. The rest of this guide is what the fix does,
for builds without an IDE and for reviewing its result.

## 2. `using` directives

| MediatR | Çapar |
|---|---|
| `using MediatR;` | `using Capar;` |
| `using MediatR.Pipeline;` | `using Capar.Pipeline;` |
| `using MediatR.NotificationPublishers;` | `using Capar.NotificationPublishers;` |
| `using MediatR.Entities;` | `using Capar.Entities;` |

The type names inside them are the same. A find and replace over the solution does it, with GNU `grep` and
`sed` (Linux, or Git Bash on Windows):

```bash
pattern='using MediatR(\.Pipeline|\.NotificationPublishers|\.Entities)?;'
grep -rlZE --include=*.cs "$pattern" . | xargs -0 -r sed -i -E "s/$pattern/using Capar\1;/"
```

It rewrites only the four namespaces in the table, `global using` lines included. Any other `MediatR`
namespace left behind, such as `MediatR.Registration`, has no Çapar counterpart and shows up as a compile
error. Check `<Using Include="MediatR" />` items in project files as well.

## 3. Registration

`AddMediatR` becomes `AddCapar`. It is declared in `Microsoft.Extensions.DependencyInjection`, as
`AddMediatR` is, so no new `using` is needed ([ADR 0007](adr/0007-addcapar-namespace.md)).

```diff
-services.AddMediatR(options =>
+services.AddCapar(options =>
 {
-    options.RegisterServicesFromAssemblyContaining<PlaceOrder>();
     options.AddOpenBehavior(typeof(Validation<,>));
     options.AddOpenRequestPreProcessor(typeof(Stamp<>));
 });
```

Delete the scanning calls, because there is nothing to scan: `RegisterServicesFromAssembly`,
`RegisterServicesFromAssemblies`, `RegisterServicesFromAssemblyContaining`, `AssembliesToRegister` and
`TypeEvaluator`. Delete `MediatorImplementationType` too: the mediator is generated, so there is no class to
swap. Everything else on MediatR's configuration exists on `CaparOptions` with the same name and number of
parameters: behaviours,
stream behaviours, processors, `Lifetime`, `NotificationPublisher`, `NotificationPublisherType`,
`AutoRegisterRequestProcessors` and `RequestExceptionActionProcessorStrategy`. A test compares the two types
member by member. Delete `RegisterGenericHandlers` and the limits that go with it
(`MaxGenericTypeParameters`, `MaxGenericTypeRegistrations`, `MaxTypesClosing`, `RegistrationTimeout`): generic
handlers are closed while the project builds, whatever they said ([ADR 0008](adr/0008-generic-handlers.md)).
A generic request handler that MediatR's default never registered is live after the migration, and two of
them for the same request fail the build with [CPR0002](diagnostics.md#cpr0002).

If you named the configuration type, `MediatRServiceConfiguration` is `CaparOptions`.

### Test projects

A test project that only resolves the application's services needs nothing: it does not reference `Capar`,
so it has no `AddCapar` of its own, and if the application gives it `InternalsVisibleTo` it can call the
application's.

A test project that references `Capar` itself, to declare test handlers, gets its own `AddCapar` in its own
namespace when the application's is visible to it ([ADR 0007](adr/0007-addcapar-namespace.md)). Code inside
the test project's namespace reaches its own; anywhere else a call can reach the application's, and the build
warns with [CPR0006](diagnostics.md#cpr0006). Calling it by its full name always works:

```csharp
global::Shop.Tests.CaparServiceCollectionExtensions.AddCapar(services);
```

## 4. Build, and read what it reports

The first build tells you what MediatR would only have told you at run time:

- [CPR0001](diagnostics.md#cpr0001): a request with no handler. MediatR would throw when it was sent.
- [CPR0002](diagnostics.md#cpr0002): a request with two handlers. MediatR silently used one of them.
- [CPR0003](diagnostics.md#cpr0003): a handler the application cannot see, usually `internal` in a library.
  MediatR's scan registered it through reflection.
- [CPR0004](diagnostics.md#cpr0004): a generic handler the build cannot close, usually one whose type parameter
  has no constraint.
- [CPR0005](diagnostics.md#cpr0005): a library that references `Capar` instead of `Capar.Contracts`.
- [CPR0006](diagnostics.md#cpr0006): an `AddCapar` call that registers another project's mediator.

Each one links to what it means and how to fix it.

## 5. Run your tests

Behaviour is held to MediatR 12.5's by tests that run the same scenarios on both libraries, including the
odd parts: which processors run, the order handlers run in, when a send throws instead of faulting its task.
[compatibility.md](compatibility.md) lists them. The differences that are deliberate:

- **Two handlers for one request is a build error** instead of a coin toss.
- **Behaviours do not run for a request nothing handles.** The send fails with the same exception type,
  without the behaviours' side effects. The build already warned with CPR0001.
- **Exception handlers are found for exception types the build can see**: those some handler for the request
  names, and `Exception`. One registered directly with the container for some other exception type is not
  found.
- **Exception messages differ.** Only exception types are part of the contract.
- **`new Mediator(serviceProvider)` does not exist.** The mediator is generated; resolve `IMediator`,
  `ISender` or `IPublisher` from the container.

## After the migration

Nothing below is needed to migrate. Each is a choice MediatR did not offer.

- **Inject the generated mediator where the call site knows what it sends.** `CaparMediator`, in the
  host's own namespace, has a typed `Send` per request that calls the handler with nothing to look up. It
  costs a quarter of MediatR's time, against about half through `ISender`
  ([measurements](benchmark-results/2026-10-17-m4.md)).
- **Make stateless handlers singletons** with `options.HandlerLifetime = ServiceLifetime.Singleton`, or by
  registering a handler yourself before `AddCapar`. The generated mediator then resolves each handler once
  and keeps it.
- **Publish with NativeAOT.** Nothing on the dispatch path uses reflection, and no trimming annotations are
  needed.
