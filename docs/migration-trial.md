# Migrating a real project

Every other test in this repository runs code written for it. This page is about code that was not: Jason
Taylor's [Clean Architecture template](https://github.com/jasontaylordev/CleanArchitecture) (MIT), a widely
used starting point for .NET applications, which uses MediatR for every request in the application layer.

## The project

- Upstream commit `1bc2b07`, 2026-10-15, .NET 10.
- MediatR **14.2.0**, the commercially licensed line, and `MediatR.Contracts` in the domain layer.
- Six projects use MediatR: Domain (`INotification` on its events), Application (handlers, an open
  pre-processor, four open behaviours, `AddMediatR`), Infrastructure (an EF Core interceptor that publishes
  domain events), Web (a global using), and two test projects.
- Tests: 6 domain unit tests, 8 application unit tests, 19 functional tests that send real commands and
  queries through the mediator against SQLite, including validation and authorization failures.

Everything ran locally with `-p:NuGetAudit=false -p:AspireUseCliBundle=false`, on both sides, for two problems
in the template's own dependencies that have nothing to do with the mediator.

## What the migration took

1. In `Directory.Packages.props`, `MediatR` became `Capar` and `MediatR.Contracts` became `Capar.Contracts`; the
   two project files changed the same names. Four lines.
2. One command for everything else:

   ```bash
   dotnet format analyzers CleanArchitecture.slnx --diagnostics CPR0007 --severity info
   ```

   It changed eight files across all six projects: seven `using` directives and the registration, which lost
   its `RegisterServicesFromAssembly` line and kept the pre-processor and the four behaviours.
3. Nothing else. The solution built with warnings as errors and no warnings.

| | MediatR 14.2.0 | Çapar |
|---|---:|---:|
| Domain unit tests | 6 passed | 6 passed |
| Application unit tests | 8 passed | 8 passed |
| Functional tests | 19 passed | 19 passed |
| Files changed | | 10, +11 / -12 lines |

## What it found

The first attempt did not go that smoothly, and each problem was Çapar's, fixed before this page was written.

- **The fix never reached the domain project.** The analyzer behind the code fix shipped with the generator in
  the `Capar` package, and Domain, like most library projects, references only `Capar.Contracts`. The analyzer
  and the fix now ship in `Capar.Contracts`, which `Capar` depends on, so every project with MediatR code gets
  them.
- **Every project below the application layer failed to build.** NuGet analyzers flow through project
  references, so Infrastructure, Web and the test projects ran the generator too, each produced an empty
  mediator of its own, and each reported Application's handlers as CPR0005, an error with warnings as errors.
  A project that has no handlers of its own, does not call `AddCapar`, and references a project with a
  mediator now generates nothing and reports nothing: it uses that project's mediator.
- **Removed lines left the next one indented twice.** The code fix carried a removed statement's indentation
  onto the next line. It now keeps only comments and directives.
- **`dotnet format` runs the fix.** Not a problem, a discovery: the command above migrates a solution without
  an IDE, which the guide now says.

None of these showed up in the repository's own tests, which reference Çapar's projects directly rather than
its packages, and which were written by the same people who wrote the code. That is the case for doing this
before a release.
