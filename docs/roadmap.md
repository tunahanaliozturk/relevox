# Roadmap

**MediatR without the license, plus everything a compiler can see.**

Relevox keeps MediatR 12's handler signatures and behaviour, so moving over is a package swap and one code fix.
What it adds comes from writing the dispatch at build time: the compiler sees every request, handler and
behaviour, and every notification type the code names, and the versions below turn that into features. A
notification type that exists only at run time, such as `EntityCreated<T>` closed inside a generic base class,
is the exception: it is dispatched at run time ([ADR 0009](adr/0009-open-notification-handlers-at-run-time.md)),
and the message map and analyzers cannot list it.

The plan lives on GitHub. Each minor version is a [milestone](https://github.com/tunahanaliozturk/relevox/milestones),
and the pinned [Roadmap issue](https://github.com/tunahanaliozturk/relevox/issues/64) explains how it works,
links every issue and lists what is not planned. Dates are estimates, and a problem reported by someone using
Relevox comes before the plan. Shipped versions are in [CHANGELOG.md](../CHANGELOG.md).

| Version | Theme |
| --- | --- |
| 1.2 | **OpenTelemetry.** Spans and metrics for `Send`, `Publish` and `CreateStream`, off by default, with names fixed at build time and no reflection. |
| 1.3 | **Message map.** A JSON and Markdown file listing each message's handler, pipeline and senders, to read and to review in pull requests. |
| 1.4 | **Architecture analyzers.** Opt-in rules for nested sends, blocking on tasks, unused cancellation tokens, requests nobody sends and layer boundaries. |
| 1.5 | **Pipeline control and validation.** Explicit behaviour order, and a FluentValidation package that adds nothing to requests without a validator. |
| 1.6 | **One feature at a time.** MediatR and Relevox run side by side through `Relevox.MediatRBridge`, so a large solution moves in small pull requests. |
| 1.7 | **`ValueTask` handlers.** An option for handlers that want it, and a code fix for moving from the Mediator library. |
| 1.8 | **Performance and scale.** A faster singleton path, allocation-free publish, and published benchmarks up to 5,000 messages. |
| 1.9 | **Developer experience.** Code fixes that write handlers, `dotnet new` templates and a documentation site. |
| 2.0 | **First major version.** What built up during 1.x cleared, with the upgrade done by one code fix. |
| 2.1 | **From MediatR 9, 10 and 11.** Code fixes for what MediatR 12 changed, so older applications move in one step. |
| 2.2 | **Publish strategies.** One after another, in parallel or continuing on error, chosen per notification at build time, with handler order. |
| 2.3 | **Ready-made behaviours.** Logging, slow request, exception and transaction behaviours, attached only to the requests that need them. |
| 2.4 | **Compile-time checks, round two.** Captive dependencies, notifications nobody handles, CQRS rules and behaviours that never call `next`. |
| 2.5 | **Performance across 2.x.** Every 2.x addition measured on a repeatable benchmark machine, and NativeAOT size reduced. |
| 2.6 | **ASP.NET Core endpoints.** `MapRequest<T>` generates NativeAOT-ready endpoints from request types. |
| 3.0 | **Second major version.** The removals announced during 2.x, with a one-command upgrade. |
| 3.1 | **Dispatch through interceptors.** Each `Send`, `Publish` and `CreateStream` rewritten at build time into a direct call to its handler. |
| 3.2 | **IDE support.** From a send to its handler and from a request to its senders, in Visual Studio and VS Code. |
| 3.3 | **From other mediators.** Code fixes for moving from other in-process mediators, picked by what users ask for. |
| 3.4 | **Testing, round two.** A test host per handler with the real pipeline, and contract tests from the message map. |
| 4.0 | **Relevox's own API?** An open question, settled during 3.x: `ValueTask` and the typed mediator by default, MediatR's shape kept as an option. |

Moving off MediatR is what Relevox is for, so the work that makes that easier comes first: endpoint
generation moved from 1.6 to 2.6 for it. Outbox, retries and other distributed messaging are not planned;
MassTransit, Wolverine and NServiceBus do that, and Relevox stays in-process. Everything up to 4.0 is
planned for mid-2027. Major versions follow breaking changes, not .NET releases: they stay on `net10.0`, and a
new .NET target comes in a minor version.

Within 1.x there are no breaking changes: package validation checks every build against the released API
([ADR 0011](adr/0011-internal-api-is-a-contract.md)), and new behaviour is opt-in.
