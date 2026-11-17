# 25. net11.0 alongside net10.0, and two packages that stay pinned

Status: accepted
Date: 2026-11-17

## Context

[ADR 0003](0003-net10-only.md) chose `net10.0` and said adding a target later is cheap if demand shows up. .NET 11
is out, and the repository is moving to its SDK. The question is what the packages target from here: `net11.0`
only, `net10.0` only, or both.

An application on .NET 11 can already use the `net10.0` assets, so this is not about permission. It is about what
has been tested. Nothing in CI ran Relevox on the .NET 11 runtime or built it with the .NET 11 SDK, so a break
specific to either would reach a user before it reached a test.

Dependabot's first attempt to refresh the dependencies also failed, with NU1109, because it bumped
`FluentValidation` without `FluentValidation.DependencyInjectionExtensions`, which has to match it. That is fixed
by moving both, and by telling Dependabot which packages to leave alone, which is the second half of this record.

## Decision

**The runtime and contracts packages, the testing, validation and bridge packages target `net10.0` and
`net11.0`.** The generator, the code fixes, the migration analyzer and the MSBuild task stay on `netstandard2.0`:
they run inside the compiler and MSBuild, not in the application.

- The tests, the web sample and the NativeAOT sample multi-target too. Benchmarks, the migration sample and the
  tools stay on `net10.0`, because a number or a tool does not gain from a second framework.
- The repository builds with the .NET 11 SDK (`global.json`). CI installs both SDKs, runs every test project
  once per framework, publishes the NativeAOT sample once per framework, and `eng/package-smoke.sh` installs the
  packed packages into an application and runs it on each.
- Packages that follow the runtime (`Microsoft.Extensions.DependencyInjection`, its abstractions and
  `Microsoft.AspNetCore.Mvc.Testing`) are 10.0.x on `net10.0` and 11.0.x on `net11.0`. The minimum versions the
  packages declare (`VersionOverride` in each project) are the same for both frameworks, so an application keeps
  choosing its own patch level.
- Package validation compares against 1.6.0, which has `net10.0` only. Adding a framework is not a break, and the
  pack passes with it.

**Two dependencies are pinned on purpose, and Dependabot ignores them.**

- `Microsoft.CodeAnalysis.CSharp` stays at 5.0.0 ([ADR 0003](0003-net10-only.md)). A generator runs inside the
  consumer's compiler, so this is the oldest compiler that can load it, which is the one in the first .NET 10 SDK.
  The .NET 11 SDK loads it too: the generator tests and the package smoke test run under SDK 11.0.100.
- `System.Reflection.Metadata` stays at 9.0.0. Transitive pinning carries it, and the `System.Collections.Immutable`
  it depends on, into the generator and the analyzers. From 10.0 on, the generator would reference a
  `System.Collections.Immutable` 10.0.0.0 that a host with an older one cannot load. Roslyn needs 9.0.0 at least.

`Microsoft.CodeAnalysis.Analyzers`, which only checks this repository's own analyzers while they are built, moved to
5.12.0 with no new warning, so it is not pinned.

## Consequences

- A break that appears only on .NET 11 fails a build here instead of in a user's project. NativeAOT is the one job
  that cannot be run on every machine, so CI is where it is checked.
- Every test run is twice as long, and every pack contains each library twice. The two assemblies are the same
  code today, and the second framework earns its place when a .NET 11 API is worth using behind a condition.
- The packed `net11.0` dependency group of `Relevox` has no `Microsoft.Extensions.DependencyInjection.Abstractions`
  entry: the SDK prunes it for that framework. The library still compiles against it, and the smoke application
  restored and ran on .NET 11.
- Dropping `net10.0` is a decision for a major version, once .NET 10 is out of support, because removing a target
  breaks the people on it. The roadmap's rule stands: a new .NET target comes in a minor version.
- The generator's floor does not move with the framework. Raising either pin is a change to what an old SDK can
  load, so it needs its own decision.

## Alternatives considered

- **`net11.0` only.** It would end support for everyone still on .NET 10, the long-term-support release, for
  nothing a library like this needs from .NET 11.
- **`net10.0` only.** Cheapest, and it works for a .NET 11 application today. It also leaves .NET 11 untested, and
  that is where a runtime change to `FrozenDictionary`, the DI container or NativeAOT trimming would show first.
- **Letting Dependabot move Roslyn and `System.Reflection.Metadata`.** Each bump would look routine and pass the
  tests, which run on the newest SDK, not on a user's oldest one. The failure would be an
  application that stops building after a package update.
