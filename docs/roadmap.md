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
| 1.6 | **ASP.NET Core endpoints.** `MapRequest<T>` generates NativeAOT-ready endpoints from request types. |
| 1.7 | **`ValueTask` handlers.** An option for handlers that want it, and a code fix for moving from the Mediator library. |
| 1.8 | **Performance and scale.** A faster singleton path, allocation-free publish, and published benchmarks up to 5,000 messages. |
| 1.9 | **Developer experience.** Code fixes that write handlers, `dotnet new` templates and a documentation site. |
| 2.0 | **First major version.** A new baseline on the next .NET LTS, with the upgrade from 1.x done by one code fix. |

Within 1.x there are no breaking changes: package validation checks every build against the released API
([ADR 0011](adr/0011-internal-api-is-a-contract.md)), and new behaviour is opt-in.
