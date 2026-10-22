# 16. The Markdown view of the message map is written by the generator, next to the JSON

Status: accepted
Date: 2026-10-22

## Context

The JSON map (ADR 0014, ADR 0015) is for tools. People read the map in pull requests, where GitHub renders
Markdown and Mermaid as they are. #45 adds a Markdown view: a table per request with its pipeline in order, and a
diagram per notification with its handlers.

Something has to turn the map into Markdown. The generator has the map as values before it writes the JSON; the
MSBuild task only has the JSON text, and it runs in .NET Framework MSBuild as well as in `dotnet build`, where
it can count on no JSON parser it does not ship.

## Decision

**The generator writes the Markdown from the same values as the JSON**, and compiles it into the assembly as a
second constant, `RelevoxMessageMap.Markdown`. The task copies it out next to the JSON under the same name with
`.md`: `relevox-message-map.md` by default, or `RelevoxMessageMapPath` with its extension changed. Like the JSON,
it is only rewritten when its content changes, and removed with the JSON from a project that has no map.

The Markdown follows the JSON's order, which is sorted, so it changes exactly when the JSON does. Types are
written as inline code, which keeps a generic's angle brackets from being read as HTML; Mermaid labels use
Mermaid's entity codes for quotes and angle brackets. A sample rendered by GitHub was checked from the
`sample/message-map-markdown` branch.

Edit cost, from `EditCostMeasurement` with the switch on (median of the last twenty of sixty edits, this
machine): the map step, which now writes both, took 7 ms per edit at 500 handlers and 51 ms at 2,500, inside the
4-12 ms and 38-58 ms ADR 0015 measured for the JSON alone.

## Consequences

- The assembly carries the map twice, a few more kilobytes of names with the switch on.
- There is no switch for the Markdown alone. A project that wants only the JSON can ignore or `.gitignore` the
  `.md` file; a switch can be added if someone asks.
- The Markdown is for reading, not parsing. Its layout may change in any release; tools read the JSON.

## Alternatives considered

- **The task renders the Markdown from the JSON.** It would need a JSON parser in a netstandard2.0 task that
  ships no dependencies (see ADR 0014), and a second reading of the map that could drift from the first.
- **A `dotnet relevox map --markdown` tool.** The same objections ADR 0014 gives for the JSON.
