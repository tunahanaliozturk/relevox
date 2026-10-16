# 3. Target net10.0 only

Status: accepted
Date: 2026-10-16

## Context

MediatR 12.5.0 targets `netstandard2.0` and `net6.0`. A replacement could aim just as wide. .NET 8 and
.NET 9 both reach end of support in November 2026, a few weeks from this decision, and .NET 6 and 7 are
already out of support.

## Decision

The runtime and contracts packages target `net10.0`. The source generator targets `netstandard2.0`,
because Roslyn loads analyzers into the compiler's own process, and references Roslyn 5.0.0, the version
that ships with the first .NET 10 SDK, so it loads on any .NET 10 SDK.

## Consequences

- `FrozenDictionary`, current `System.Threading` primitives and the C# 14 compiler are available
  without polyfills.
- Projects still on .NET 8 cannot use this until they upgrade. That is a real group for a few more weeks
  and a shrinking one after.
- Adding a target later is cheap if demand shows up; removing one after people depend on it is not.

## Alternatives considered

- **netstandard2.0 for the contracts.** It would let .NET Framework domain projects reference the
  marker interfaces. Nobody has asked, and it can be added without breaking anyone.
