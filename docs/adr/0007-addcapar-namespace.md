# 7. `AddCapar` lives in `Microsoft.Extensions.DependencyInjection`

Status: accepted
Date: 2026-10-17

## Context

The generator writes `AddCapar` into the project that calls it. Until M5 it went into a namespace named after
the project, next to the generated mediator, because two projects that both reference Çapar, one with
`InternalsVisibleTo` the other, must not declare the same type name.

Every test in the repository used project references and happened to have a `using` for that namespace, so
nothing failed. Packing the packages and installing them into a new console app did: top-level statements are
in the global namespace, `services.AddCapar()` did not compile, and the fix was a `using` the user had no way
to guess. MediatR's `AddMediatR` is in `Microsoft.Extensions.DependencyInjection`, which ASP.NET Core and
worker projects import implicitly, so a migrated `Program.cs` would break on its first line.

## Decision

The class holding `AddCapar` is declared in `Microsoft.Extensions.DependencyInjection`. The generated
mediator stays in the project's own namespace.

When the project can see another project's generated `AddCapar` there, through `InternalsVisibleTo`, its own
moves back to the project's namespace. Two methods with one signature in one namespace would make every call
ambiguous (CS0121). The usual case is a test project that references `Capar` and tests an app that grants it
access to its internals. Inside the test project's namespace its own method is found first; from the global
namespace the app's is.

`eng/package-smoke.sh` now packs both packages and builds an application against the `.nupkg` files in CI.
Its `Program.cs` imports only `Capar`, `Microsoft.Extensions.DependencyInjection` and its own types'
namespace, which is not the generated one, so a mistake of this kind fails the build.

## Consequences

- A `Program.cs` moved from MediatR needs no new `using` for registration, and neither does a new one.
- The generated class name, `CaparServiceCollectionExtensions`, now sits in a namespace many assemblies
  share. It is `internal`, so it clashes only with an assembly that grants this one access, and the fallback
  above covers the common shape of that. Two shapes it leaves to the user:
  - A file outside the project's namespace that imports both the project's namespace and
    `Microsoft.Extensions.DependencyInjection` sees two `AddCapar` methods and gets CS0121.
  - A project that two others grant access to sees both of theirs in the shared namespace: an unqualified call
    is ambiguous, and naming the class is CS0433.

  In both, the project's own method is reachable by its full name,
  `global::<ProjectNamespace>.CaparServiceCollectionExtensions.AddCapar(services)`.
- A call that compiles but binds to another project's `AddCapar` would leave this project's handlers
  unregistered with nothing said at build time. The analyzer reports it as
  [CPR0006](../diagnostics.md#cpr0006) and names the call to use instead.
- Code that named the class to call it statically, as the differential tests do, names the new namespace.

## Alternatives considered

- **Leave it in the project namespace and document the `using`.** Every new user would hit it first, on the
  one line the migration guide promises is a rename.
- **A public, hand-written `AddCapar` in the runtime package that finds the generated code.** It would need
  reflection or a registration hook to reach the generated method, which works against trimming and NativeAOT.
- **A per-assembly class name in the shared namespace.** Avoids the type name clash but not the ambiguous call,
  which is about the method, so it fixes nothing the fallback does not.
