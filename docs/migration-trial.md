# Migrating real projects

Every other test in this repository runs code written for it. This page is about code that was not: two widely
used .NET applications on MediatR, moved to Relevox the way the [migration guide](migration.md) says, with their
own tests run before and after.

- Jason Taylor's [Clean Architecture template](https://github.com/jasontaylordev/CleanArchitecture) (MIT), on
  MediatR 14.2.0.
- Microsoft's [eShop reference application](https://github.com/dotnet/eShop) (MIT), on MediatR 13.1.0.

## Clean Architecture

### The project

- Upstream commit `1bc2b07`, 2026-10-15, .NET 10.
- MediatR **14.2.0**, the commercially licensed line, and `MediatR.Contracts` in the domain layer.
- Six projects use MediatR: Domain (`INotification` on its events), Application (handlers, an open
  pre-processor, four open behaviours, `AddMediatR`), Infrastructure (an EF Core interceptor that publishes
  domain events), Web (a global using), and two test projects.
- Tests: 6 domain unit tests, 8 application unit tests, 19 functional tests that send real commands and
  queries through the mediator against SQLite, including validation and authorization failures.

Everything ran locally with `-p:NuGetAudit=false -p:AspireUseCliBundle=false`, on both sides, for two problems
in the template's own dependencies that have nothing to do with the mediator.

### What the migration took

1. In `Directory.Packages.props`, `MediatR` became `Relevox` and `MediatR.Contracts` became `Relevox.Contracts`; the
   two project files changed the same names. Four lines.
2. One command for everything else:

   ```bash
   dotnet format analyzers CleanArchitecture.slnx --diagnostics RVX0007 --severity info
   ```

   It changed seven source files: six `using` directives and the registration, which lost
   its `RegisterServicesFromAssembly` line and kept the pre-processor and the four behaviours.
3. Nothing else. The solution built with warnings as errors and no warnings.

| | MediatR 14.2.0 | Relevox |
|---|---:|---:|
| Domain unit tests | 6 passed | 6 passed |
| Application unit tests | 8 passed | 8 passed |
| Functional tests | 19 passed | 19 passed |
| Files changed | | 10, +11 / -12 lines |

### What it found

The first attempt did not go that smoothly, and each problem was Relevox's, fixed before this page was written.

- **The fix never reached the domain project.** The analyzer behind the code fix shipped with the generator in
  the `Relevox` package, and Domain, like most library projects, references only `Relevox.Contracts`. The analyzer
  and the fix now ship in `Relevox.Contracts`, which `Relevox` depends on, so every project with MediatR code gets
  them.
- **Every project below the application layer failed to build.** NuGet analyzers flow through project
  references, so Infrastructure, Web and the test projects ran the generator too, each produced an empty
  mediator of its own, and each reported Application's handlers as RVX0005, an error with warnings as errors.
  A project that has no handlers or processors of its own, does not call `AddRelevox`, and references a project
  with a mediator now generates nothing and reports nothing about its references: it uses that project's
  mediator.
- **Removed lines left the next one indented twice.** The code fix carried a removed statement's indentation
  onto the next line. It now keeps only comments and directives.
- **`dotnet format` runs the fix.** Not a problem, a discovery: the command above migrates a solution without
  an IDE, which the guide now says.

## eShop

### The project

- Upstream commit `913acea`, 2026-10-15, .NET 10, Aspire.
- MediatR **13.1.0**, on the commercially licensed 13.x line. eShop sets no licence key.
- The ordering service is the part that uses MediatR. Domain declares seven domain events as `INotification`.
  Infrastructure publishes them through `IMediator` from its `DbContext`. Ordering.API holds nine command and
  query handlers, seven domain event handlers, three open behaviours (logging, validation, a database
  transaction) and the `AddMediatR` call. The unit tests mock `IMediator`.
- Tests: 43 ordering unit tests, and 11 functional tests that start the API against a real Postgres in a
  container through Aspire and send it HTTP requests.

The same `-p:NuGetAudit=false -p:AspireUseCliBundle=false` as above, on both sides. The solution file includes
the MAUI client, which needs workloads this machine does not have, so the commands use eShop's own
`eShop.Web.slnf`, which leaves it out.

### What the migration took

1. In `Directory.Packages.props`, `MediatR` became `Relevox` and `Relevox.Contracts`. Ordering.Domain's project
   file changed `MediatR` to `Relevox.Contracts`, and Ordering.API, which had MediatR only through Domain, got
   an explicit reference to `Relevox`, since the generator has to run where `AddRelevox` is called.
2. The same command:

   ```bash
   dotnet format analyzers eShop.Web.slnf --diagnostics RVX0007 --severity info
   ```

   It changed five source files in 26 seconds: four global `using` directives and the registration, which lost
   its `RegisterServicesFromAssemblyContaining` line and kept the three behaviours.
3. Nothing else. eShop builds with warnings as errors, and it built with none.

| | MediatR 13.1.0 | Relevox |
|---|---:|---:|
| Ordering unit tests | 43 passed | 43 passed |
| Ordering functional tests, Postgres | 11 passed | 11 passed |
| Files changed | | 8, +9 / -9 lines |

The count leaves out the local package feed added to `nuget.config` for the trial, which a real migration
would not need. The fix does not touch project files, so a `<!-- Before license change -->` comment in
`Directory.Packages.props` was left sitting above the `Relevox` line.

Only Ordering.API generates a mediator. The two test projects reference it, run the generator because analyzers
flow through project references, and generate nothing, as the consumer rule from the first trial intends.

### What it found

Less than the first one, which is the point of doing a second.

- **A blank line at the top of the registration.** The scan call was the first statement in the lambda and a
  blank line followed it; removing the call left the block opening with that blank line. The fix now drops
  blank lines that would open a block. The table above is from the fixed version.
- **The build does not show RVX0007.** Not a bug, but worth saying: after the package swap, `dotnet build` shows
  only the compiler's errors for the missing `MediatR` namespace, because the compiler does not run analyzers
  once there are errors. The IDE and `dotnet format` do, which the guide now says.
- **`dotnet format` only changes files below the directory it runs in.** Found writing the package smoke test,
  not in eShop, and also in the guide now.

### The message map

Later, for 1.3, the migrated eShop was built again with `-p:RelevoxMessageMap=true` against packages from the
1.3 branch, and the map Ordering.API wrote was checked against the code by hand.

- **Requests.** Nine request types: eight commands, each with its handler, and `IdentifiedCommand<T, R>`, the
  idempotency wrapper. The wrapper is listed once for each command the build found it closed over, seven times,
  each with its `IdentifiedCommandHandler` subclass. All right.
- **Behaviours.** The three open behaviours on every request, in registration order: logging, validation,
  transaction. All right.
- **Notifications.** The seven domain events from Ordering.Domain, each with its one handler from Ordering.API.
  All right.
- **Call sites.** Nine calls listed, each on the line of a real `Send`: four in `Apis/OrdersApi.cs` and five in
  the integration event handlers. Two calls are left out, as `docs/message-map.md` says they are: the send
  inside `IdentifiedCommandHandler`, whose argument is the type parameter `T`, and the `Publish` of domain
  events, which is in Ordering.Infrastructure, another project.

Nothing in the map was wrong, and nothing the build could have known was missing.

### The architecture rules

For 1.4, both migrated projects were built again against packages from the 1.4 branch, with the architecture
rules raised to warnings in `.editorconfig` so that every report showed.

- **RVX0008, a handler that sends.** One report, in eShop: `IdentifiedCommandHandler` sends the command it
  wraps. That is the pattern the rule describes, used on purpose, and the rule page uses it as its example of
  where to suppress it. Clean Architecture has none.
- **RVX0009, send cycles.** None in either. The send in `IdentifiedCommandHandler` is of the type parameter `T`,
  which the rule does not follow.
- **RVX0010, blocking on a task.** None in either, and none missed: the one `GetAwaiter().GetResult()` in Clean
  Architecture is in an EF Core interceptor, outside the pipeline, and the `.Result` in eShop's
  `LinqSelectExtensions` is a property of its own result type, not of a task.
- **RVX0011, publishing from the wrong layer.** Clean Architecture with its four layers in `.editorconfig` and
  `BaseEvent` allowed from Infrastructure: no reports, since its one `Publish` is in
  `DispatchDomainEventsInterceptor`. With `BaseEvent` allowed only from Web instead, that call was reported, and
  a `publish_from` with a misspelt layer name was reported as RVX0012.

## Why bother

None of what the first trial found showed up in the repository's own tests, which reference Relevox's projects
directly rather than its packages, and which were written by the same people who wrote the code. That is the
case for doing this before a release, and for doing it on more than one project.
