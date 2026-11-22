# 30. The dotnet new templates

Status: accepted
Date: 2026-11-22

## Context

A new project started from the README: install the package, write a request and a handler, call `AddRelevox`,
map an endpoint, add a test project. 1.9 ships `dotnet new` templates for that, in one package,
`Relevox.Templates`: `relevox-webapi` for a new solution and `relevox-feature` for a request added to one.

Several parts of that have more than one reasonable answer.

## Decision

**`relevox-feature` writes into two projects in one run.** It is run in the folder that holds the application and
its test project, named `<app>` and `<app>.Tests`, which is what `relevox-webapi` creates. `--app` names the
application; the request and handler go to `<app>/<name>.cs` in namespace `<app>`, the test to
`<app>.Tests/<name>Tests.cs` in namespace `<app>.Tests`. `--response` is the response type, `string` by default.
The handler answers `default` and the test checks that, so the generated test passes and is the first thing to
change.

**`relevox-webapi` targets `net10.0` by default and offers `net11.0`.** `net10.0` builds with both SDKs and is the
long-term support release. `--framework net11.0` is there because the packages target it. Both are instantiated,
built and tested in CI. The endpoint is mapped by hand: endpoint generation is planned for 2.6, and the template
should not show an API that does not exist yet.

**The generated tests are xUnit v3 on Microsoft.Testing.Platform, as in this repository.** The solution carries a
`global.json` with only the test runner in it, so `dotnet test` works on the .NET 10 and 11 SDKs without pinning
either.

**The generated project references the Relevox version the templates were packed with.** The project file in the
template carries a token, and the pack replaces it with `$(Version)` in a copy under `obj` and packs the copy. A
template package and the packages it references are always released together, so the version written in is one
that exists on nuget.org.

**The template project is not in `Relevox.slnx`.** The `.cs` files under `content/` are templates, not code of
this repository: placeholders such as `RelevoxResponse` are not types, and the generated files follow the
formatting a new project gets, not this repository's `.editorconfig`. Out of the solution, the solution's build
and `dotnet format` never see them. `eng/package-smoke.sh` packs the project, installs the package into a hive of
its own, instantiates both templates outside the repository, and runs `dotnet test` on the result with warnings
as errors, once per framework. That runs in the existing `package smoke test` CI job, and the release workflow
publishes the package with the others because it is packed into the same feed.

**Package validation is off for this package.** It has no assembly, so there is no API to compare with a baseline,
and no symbols package.

## Consequences

- `dotnet new install Relevox.Templates` gets the templates for the version released with it; a later Relevox
  release means a later template package.
- A solution laid out differently from `<app>` and `<app>.Tests`, for example under `src/` and `tests/`, gets the
  feature files in new folders and has to move them.
- The xUnit and `Microsoft.AspNetCore.Mvc.Testing` versions in the template are written by hand and are not
  updated by Dependabot. They are checked when CI restores them, and with warnings as errors a version with a known
  vulnerability fails the smoke test.
- A request with no response (`IRequest`) is not a choice in `relevox-feature` yet.

## Alternatives considered

- **`relevox-feature` as an item template run twice, once in each project with a switch for the test.** Works
  with any layout, but two runs with the same arguments are easy to get wrong, and the first one leaves a
  request with no test.
- **Folders for the application and the test project as parameters, so any layout works.** A path parameter is
  also the namespace, so it needs a second parameter for that; more to type for the case the template exists to
  make short.
- **The template project in `Relevox.slnx`, with every file removed from compilation.** Visible in the IDE, but
  one wrong glob would compile the placeholders or format them against the wrong rules, and it gains nothing a
  build of the solution needs.
- **The version stamped by the smoke script before packing.** CI and the release pack through the script, but a
  pack by hand would then write a wrong version into the package.
- **`net11.0` as the default.** Fails to build on a .NET 10 SDK, which many machines still have.
