# 21. Projects using another project's mediator write their calls, and a jq recipe combines the maps

Status: accepted
Date: 2026-10-26

## Context

#51 asked for a rule that reports a request nothing sends. Neither an analyzer nor the message map could do it,
because both see one project. In the migrated Clean Architecture, `AddRelevox` is called in Application, which
declares the requests, and every `Send` is in Web, which references Application. Application's compilation
sees no sends, so every request would be reported; and only Application wrote a map (ADR 0014), listing only
its own calls (ADR 0017). Web, with the switch on, wrote nothing.

So the check needs a view of the whole solution, which no single compilation has (#89).

## Decision

**A project that uses another project's mediator writes a map of the calls it makes.** With
`RelevoxMessageMap` on, a project whose generator writes no mediator now writes a map when it has at least one
call site: the same schema version 1 document with empty `requests`, `streams`, `notifications` and `unread`,
plus a new top-level field, `calls`, with one entry per message type and that type's call sites, read as ADR
0017 reads them. A project with no calls still gets no files, and the task still removes stale ones, so
turning the switch on for a whole solution adds files only where calls are.

The document stays a valid map rather than becoming a second format. A tool that reads `*message-map.json`
sees a map that dispatches nothing and ignores the field it does not know, which the schema's compatibility
rule already asks of it. The mediator's own map does not get `calls`: its calls to its own messages are under
each message, as before, and a request with no handler is RVX0001's to report.

A call whose argument is an interface or an abstract class is now left out where it is read. The mediator's
map already left those out, because they matched none of its messages; a consumer's map has nothing to match
against, so the rule moved to the reader.

**The maps are combined after the build, by a jq program in the repository,
[docs/message-map-combine.jq](../message-map-combine.jq).** It takes every map, keeps one entry per mediator,
and gives each message the call sites of every map that sends it, each with a `project` field naming the
project the call is in. The check is one more jq line over that output: the requests whose `callSites` is
empty. jq is on GitHub's hosted runners, and the program is short enough to copy.

`MessageMapTests` runs the program over maps the generator wrote for a mediator and two consumers, covering a
request sent by the mediator's project, by one consumer, by both and by nobody, and runs the check as the docs
write it. The package smoke test does the same with real builds of its layered host.

Cost: with the switch off nothing changes; the map step still yields null, and the generated code is what it
was (`With_the_switch_off_the_generated_code_is_what_it_was`). With it on, a consumer's map step sorts its call
sites and writes them, without the plan work a mediator's map does. Not measured separately: the machine was
busy when this was written, and the step adds no provider to the ones ADR 0017 measured.

## Consequences

- Turning the switch on in `Directory.Build.props` now writes a map in every project that sends, test projects
  included. A request sent only from tests counts as sent. A team that does not want that leaves the test
  projects' maps out of the combine.
- Matching is by type name across maps. Two mediators in one solution that dispatch the same message type both
  get the call; that is what the solution's code can do, so the view says so.
- The check finds requests no project sends by their static type. One sent only as `object`, as an interface,
  or from a generic method is reported, as the map cannot see those calls (docs/message-map.md lists them).
- The combined view is not written by the build. It exists only where the jq step runs, which keeps the build
  free of a solution-wide step MSBuild has no natural place for.

## Alternatives considered

- **A call-site file of its own format.** Cleaner to read, but a second schema, a second file name and a second
  CI pattern, for a document that fits the first.
- **The mediator's build reads its consumers' maps.** The consumers build after it, since they reference it, so
  its map would always be one build behind.
- **A dotnet tool or MSBuild target that combines the maps.** Users would install and pin it, or wire a target
  into a solution-level build, for what a dozen lines of jq do. It can still come later, reading the same
  files.
- **An analyzer.** It sees one compilation, which is the problem this solves.
