# 17. The message map records where each message is sent from

Status: accepted
Date: 2026-10-22

## Context

The map says which handler runs and what runs around it. The other half of the question someone reading a
pull request asks is where a message is sent from, which today takes a "find usages" across the solution (#46).

## Decision

**A call site is a `Send`, `Publish` or `CreateStream` call the compiler binds to `ISender`, `IPublisher`,
`IMediator` or a class implementing one of them, whose message argument has a type where it is written.** The
generator finds them with a syntax provider: a cheap filter on the method name, then the semantic model for the
calls it lets through. Each call is read on its own, so an edit rereads only the calls in the file it changed,
and an edit that adds, removes or moves no call leaves the map step's input as it was.

The argument's static type is matched against the map's messages by name. That leaves out, without a rule of
its own, everything the build cannot place: `Send(object)`, a message held as `IRequest<int>` or as a base
class, and generic type parameters. Calls through a wrapper or an extension method are not seen either; the map
says so rather than guessing.

**Each call is a file and a line.** The file is relative to the project directory (`build_property.ProjectDir`,
which the SDK passes to every generator), with `/` between folders, so the map is the same on every machine and
never carries an absolute path into the assembly (ADR 0014's consequence). A file outside the project folder is
written by its name. The line is the method name's, which for a call chained over several lines is the line that
sends. Calls are sorted by file, then line.

**The map is per project**, as before: a call from another project is in that project's map.

Edit cost, from `EditCostMeasurement` (median of the last twenty of sixty edits, this machine, which was noisy):
with the switch off the map step still takes no measurable time; with it on it took 13 ms per edit at 500
handlers and 62 ms at 2,500, against 7 ms and 51 ms before call sites. The measurement's project has no calls,
so this is the cost of the extra provider and the join, not of reading calls.

## Consequences

- Moving a call to another line changes the map. A committed map changes with edits above a call, which is the
  price of a line number a reader can jump to; `file` alone would be steadier and less useful.
- The call site provider runs with the switch off too, since a syntax provider cannot depend on a build
  property. It only does work for calls named `Send`, `Publish` or `CreateStream` in edited files.

## Alternatives considered

- **Walk every syntax tree in the map step.** Simpler to wire, but it reruns over the whole compilation on every
  edit with the switch on.
- **Record the containing method instead of the line.** Steadier across edits, but it needs a name for lambdas
  and local functions and is a step further from what an editor can open.
