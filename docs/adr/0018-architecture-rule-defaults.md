# 18. Architecture rules are Info by default and ship with the contracts

Status: accepted
Date: 2026-10-24

## Context

1.4 adds rules about how an application uses its mediator: a handler that sends another request (RVX0008),
handlers that send each other in a cycle (RVX0009), and more to follow in the same milestone. Unlike RVX0001 to
RVX0006 they do not say the code cannot dispatch. Each points at code that compiles, runs, and is sometimes
exactly what was meant: eShop's idempotency wrapper sends the command it wraps, and a tree walk can send the
same request once per node and stop at the leaves.

Two questions follow. How loud should such a rule be when a project first upgrades? And which analyzer carries
it, given that RVX0001 to RVX0006 ship with the generator in `Relevox`, which only the application references,
while handlers mostly live in libraries that reference only `Relevox.Contracts`?

The issue for RVX0008 (#48) asked for a cycle to be a warning, and also for no architecture rule to break an
existing build. Those conflict: many projects, this one included, build with warnings as errors, and a minor
release that adds a warning to code that built yesterday breaks that build.

## Decision

**Every architecture rule is Info by default**, cycles included. An IDE shows it as a suggestion; a command-line
build gets no new warning from an upgrade. A team that wants a rule enforced raises it in `.editorconfig`
(`dotnet_diagnostic.RVX0009.severity = error`), and one that disagrees with it sets `none`.
`dotnet_analyzer_diagnostic.category-Relevox.severity` covers every Relevox rule at once.
[docs/diagnostics.md](../diagnostics.md) shows both.

A rule that needs configuration to mean anything, such as the layer rule RVX0011 (#52), is on by default and
reports nothing until it is configured. A configuration it cannot read is reported, not ignored, as a warning
(RVX0012): only a project that wrote the setting can get it.

A rule may be made louder in a major version, never in a minor one.

**They ship in `Relevox.Contracts`**, in the analyzer assembly that already carries RVX0007
(`Relevox.Migration`), so they run in every project that declares handlers. The assembly keeps its name: it is
not part of any public API, and renaming it would only churn the package layout.

**Each rule pays for itself in analysis time.** Every rule's PR measures it with
`tests/Relevox.SourceGenerator.Tests/AnalyzerCostMeasurement.cs` on 2,500 handlers, against the dispatch
analyzer measured the same way.

## Consequences

- Upgrading to 1.4 changes no build's outcome. The cost is that a rule nobody raises is easy to miss in CI; the
  rule pages and the changelog say how to raise each.
- A rule that turns out to be noisy can be switched off with one line, and is never a reason to hold an upgrade
  back.
- Rules that need the whole application, such as a request nobody sends (#51), see only one compilation. Where
  that makes an analyzer unreliable, the check is built on the message map instead.
