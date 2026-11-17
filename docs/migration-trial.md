# Migrating real projects

Every other test in this repository runs code written for it. This page is about code that was not: widely used
.NET applications, moved to Relevox the way the migration guides say, with their own tests run before and after.

- Jason Taylor's [Clean Architecture template](https://github.com/jasontaylordev/CleanArchitecture) (MIT), on
  MediatR 14.2.0.
- Microsoft's [eShop reference application](https://github.com/dotnet/eShop) (MIT), on MediatR 13.1.0.
- neozhu's [Blazor Clean Architecture template](https://github.com/neozhu/CleanArchitectureWithBlazorServer)
  (MIT), on Mediator 3.0.2, the source-generated library, following [its own guide](migration-from-mediator.md).

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

### Relevox.FluentValidation

For 1.5, Clean Architecture's own `ValidationBehaviour<,>` was replaced by the package, built from the 1.5 branch
([validation.md](validation.md)). Its body became a class derived from the package's behaviour that overrides
`CreateException` to throw the template's own `ValidationException`, so its API's problem details stay as they
were, and `cfg.AddOpenBehavior(typeof(ValidationBehaviour<,>))` became `cfg.AddValidation(typeof(ValidationBehaviour<,>))`
in the same place. The 19 functional tests passed before and after, the validation ones included. The message
map shows the behaviour on the four commands that have a validator and on none of the other five requests,
where it used to run, find no validator, and call the next behaviour.

### The whole solution's calls

Also for 1.5, Clean Architecture's Web project was built with `-p:RelevoxMessageMap=true`, which reaches
Application through the project reference, against packages from the 1.5 branch. Before, only Application
wrote a map, and it had no call sites: every `Send` is in Web. Now Web writes one too, listing nine calls, each
on the line of a real `Send` in `Endpoints/`. The two maps put through
[message-map-combine.jq](message-map-combine.jq) give each of Application's nine requests its call from Web,
and the [check for requests nothing sends](message-map.md#requests-nothing-sends) reports none.

## Clean Architecture, one feature at a time

The same template, from the same upstream commit on MediatR 14.2.0, moved a second time through
`Relevox.MediatRBridge` ([one feature at a time](migration.md#one-feature-at-a-time)), in six steps. After each
step the solution built with warnings as errors, and all 33 tests passed: 6 domain, 8 application, 19
functional.

| Step | What changed | Files | RVX0014 / RVX0016 after it |
|---|---|---:|---|
| 1 | `Relevox` and `Relevox.MediatRBridge` added, `AddRelevox()` and `AddMediatRBridge()` after `AddMediatR`. Nothing moved. | 3 | handlers 10, behaviours 4, processors 1 |
| 2 | The four behaviours and the logging pre-processor implement Relevox's interfaces as well, and `AddRelevox` registers them in the same order. | 6 | handlers 10, behaviours 0, processors 0 |
| 3 | TodoLists: four requests carry both interfaces, their handlers implement Relevox's. | 4 | handlers 6, behaviours 0, processors 0 |
| 4 | TodoItems and the domain events: four requests, `BaseEvent`, and the one event handler. Domain gains `Relevox.Contracts`. | 7 | handlers 1, behaviours 0, processors 0 |
| 5 | WeatherForecasts, the last query. | 1 | RVX0016: the bridge can go |
| 6 | MediatR removed: its interfaces and the MediatR overloads deleted, the registration and the packages removed, then the RVX0007 fix for the `using` directives. | 23 | |

The functional tests send through MediatR's `ISender` until step 6, so from step 3 they exercise the bridge.
That the moved requests really ran Relevox's pipeline was checked by taking `ValidationBehaviour` out of
`AddRelevox` after step 3: exactly the three TodoLists validation tests failed, and the TodoItems ones, still
on MediatR, passed.

### What it found

- **RVX0015 warned before anything had moved.** As first written it fired wherever the bridge was referenced,
  so step 1, which moves nothing, failed under warnings as errors with one warning per behaviour. It now waits
  until the project has a Relevox handler or a message with both interfaces.
- **A behaviour needs its body once and two `Handle` methods.** The two libraries' delegates are different
  types, so a behaviour that serves both forwards each to one private method taking `Func<Task<TResponse>>`.
  A pre-processor needs nothing of the sort: both interfaces have the same `Process` method, which serves both.
- **The last step is the largest.** Removing MediatR touched 23 files, most of them to delete the half of each
  type the bridge needed. The RVX0007 fix could not do it alone: run on a type carrying both interfaces, it
  would rename MediatR's to Relevox's and leave the interface twice.

## Blazor Clean Architecture on Mediator

### The project

- [neozhu/CleanArchitectureWithBlazorServer](https://github.com/neozhu/CleanArchitectureWithBlazorServer), upstream
  commit `292a8cb`, 2026-11-13, .NET 11.
- Mediator **3.0.2**: `Mediator.SourceGenerator` and `Mediator.Abstractions` in Application,
  `Mediator.Abstractions` in Domain.
- Domain declares its events as `INotification`. Application holds 40 request handlers, 14 notification
  handlers (one of them for three events), four behaviours, four `MessageExceptionHandler` subclasses, two notification publishers of its own
  and the `AddMediator` call, with a scoped lifetime and the publisher that queues handlers on a channel and
  returns at once. Two EF Core interceptors in Infrastructure publish through `IMediator`, the Blazor UI injects
  it in Razor, and three test files call `AddMediator` themselves.
- Tests: 33 application unit tests, among them the publisher's own tests and smoke tests that publish real events
  through the mediator, and 20 integration tests that send commands and queries through it against SQL Server
  LocalDB. `Domain.UnitTests` has no tests. `Infrastructure.UnitTests` is not in the solution and does not build
  upstream (it targets `net10.0` and references `net11.0` projects), so it is left out on both sides.

Everything ran with `-p:NuGetAudit=false`, on both sides.

**Why this one.** Of the applications a GitHub code search for `Mediator.SourceGenerator` 3.x turned up, it is the
most starred, MIT licensed and kept current (moved to .NET 11 four days before the trial). It also uses the
parts where Mediator and Relevox differ most: a lifetime other than the default, exception handlers, a
notification publisher of its own, behaviours with constraints, and scaffolded code. Passed over: ardalis's
RiverBooks, which has tests but no licence in the repository; neozhu's cleanaspire and
minhsangdotcom/clean-architecture, smaller and not updated since July and May; and Mediator's own samples,
written by the library's author.

### What the migration took

1. The package swap: Application's two Mediator package references, five lines, became one for `Relevox`, and
   Domain's became `Relevox.Contracts`.
2. The Contacts feature was scaffolded with an `<auto-generated>` header on every file, so `.editorconfig` got a
   `generated_code = false` section for it ([step 2 of the guide](migration-from-mediator.md#2-scaffolded-files)).
3. One command:

   ```bash
   dotnet format analyzers CleanArchitecture.Blazor.slnx --diagnostics RVX0017 --severity info --include-generated
   ```

   It took about a minute and changed 67 files, 51 of them in `Features`: the `using` directives, every handler
   and behaviour, the registration, and the three registrations in the tests.
4. By hand, 15 files, each a case the [guide](migration-from-mediator.md#4-what-the-fix-leaves) describes: the
   four exception handlers (three to `IRequestExceptionHandler`, the catch-all to a behaviour), the two
   publishers and their tests, a Moq setup that returned `ValueTask.CompletedTask`, two `_Imports.razor` files,
   and the three test registrations, which now call the application's `AddApplication()`.

| | Mediator 3.0.2 | Relevox |
|---|---:|---:|
| Application unit tests | 33 passed | 33 passed |
| Application integration tests, SQL Server LocalDB | 20 passed | 20 passed |
| Files changed by the fix | | 67, +109 / -116 lines |
| Files changed by hand | | 15, +90 / -89 lines |

The counts leave out the local package feed in `nuget.config` and the `.editorconfig` section, which a real
migration would remove afterwards.

The template's validation and not-found tests do not await their assertions, so they pass whatever the
exception handlers do. To see what they do, a test written for the trial and not kept sent an invalid command
and asked for a contact that does not exist, on both sides. Mediator answered `'Text' must not be empty. | 'Value'
must not be empty.` and `An unexpected error occurred: Sequence contains no elements.`, and Relevox, at the end,
the same.

### What it found

More than the two MediatR trials together, as expected of a library further from Relevox. What changed in
Relevox is in the code fix and its tests; the rest is in the guide.

- **The fix skipped a whole feature.** Twelve files of the Contacts feature start with `// <auto-generated>`,
  and Roslyn does not run an analyzer on such files unless it asks to, so RVX0017 never pointed at them and
  their handlers kept `ValueTask`. RVX0017 and RVX0007 now ask, and still leave alone what Roslyn's file rules
  say a tool wrote, such as a generator's `*.g.cs`. That is not the whole answer: *Fix all*, and so
  `dotnet format`, skip a file Roslyn considers generated whatever the analyzer does. `--include-generated` alone
  changed nothing, and nor did `generated_code = false` alone; the two together moved the feature, which the
  guide now says.
- **One publisher of its own kept the whole registration back.** The fix gave up on an `AddMediator` naming a
  publisher it did not know, so the registration, lifetime and behaviours were left for a person. It now keeps
  the publisher line as it is and moves the rest; the publisher class is the compiler's to point at.
- **The exception handlers were in the list of behaviours.** Moved as written, they would have become
  `AddOpenBehavior` calls, and `AddOpenBehavior` throws for a type that is not a behaviour, which they are not
  once they are Relevox exception handlers. The fix now leaves `MessageExceptionHandler` subclasses out:
  Relevox finds exception handlers at build time.
- **`next(request, cancellationToken).AsTask()`.** The caching behaviour hands `next` to FusionCache, which
  wants a `Task`. The fix dropped the message and kept the `.AsTask()`, which `Task` does not have. It now drops
  both.
- **Three handlers returned a private `ValueTask` helper.** ADR 0024 left any such return for the compiler,
  reasoning that its type went with the package. That is true of Mediator's types, not of the project's own,
  which still resolve; the fix now adds `.AsTask()` to them.
- **Razor.** Two `_Imports.razor` files had `@using Mediator`, and code fixes cannot reach Razor. 267 build
  errors came from those two lines. In the guide.
- **Generic exception handlers with a fixed exception type are not registered.** Rewritten in the obvious way,
  as `DbExceptionHandler<TRequest, TResponse> : IRequestExceptionHandler<TRequest, TResponse,
  DbUpdateException>`, all four built and were reported as [RVX0004](diagnostics.md#rvx0004), a warning, and not
  registered. Relevox registers a generic exception handler only when its type parameters are exactly the
  interface's, so the exception type became a third type parameter constrained to the class. In the
  guide; closing such handlers at build time would be new generator work, not done here.
- **The catch-all answered first.** As an exception handler for `Exception`, the fallback was also tried at the
  `ValidationException` step, and came before the validation handler in metadata order, so the check above got
  the fallback's messages, `Text: 'Text' must not be empty.`. In Mediator it stood first in the list of
  behaviours and only saw what the others left. As a behaviour with `[PipelineOrder(-1)]`, outside Relevox's
  exception handlers, it gives Mediator's answer again. Relevox's order is MediatR 12's and stays; the guide says
  how to keep Mediator's.
- **Test projects called `AddMediator`.** Mediator's is generated once and public, so the tests configured the
  application's mediator. After the fix each test project called its own `AddRelevox`: the integration tests
  threw `IMediator is already registered` in their setup, and six unit tests timed out waiting for a
  notification that reached no handler, with an [RVX0005](diagnostics.md#rvx0005) warning saying why. They now
  call `AddApplication()`, as the host does. The unit test project keeps RVX0005, because its publisher tests
  declare handler classes of their own; the RVX0005 page now covers that case.

## Why bother

None of what the first trial found showed up in the repository's own tests, which reference Relevox's projects
directly rather than its packages, and which were written by the same people who wrote the code. That is the
case for doing this before a release, and for doing it on more than one project.
