# Contributing

## Getting set up

You need the .NET 10 SDK. Nothing else.

```bash
dotnet build Capar.slnx
dotnet run --project tests/Capar.SourceGenerator.Tests
dotnet run --project tests/Capar.DifferentialTests
dotnet run -c Release --project benchmarks/Capar.Benchmarks -- --sanity
dotnet run --project tools/Capar.LicenseAudit -- .
bash eng/package-smoke.sh
```

The last one packs both packages and builds a new application against them. Run it after touching anything
the package carries: the generator, project files, `Directory.Build.props`.

## Ground rules

**Warnings are errors.** Every project builds with `TreatWarningsAsErrors` and enforced code style. Run
`dotnet format` before you push; CI checks whitespace and style.

**Behaviour matches MediatR 12.5.0 unless an ADR says otherwise.** The point of this library is that
moving to it changes nothing a caller can observe. A change to pipeline ordering, exception handling or
publish semantics needs a differential test showing both libraries agree, or an ADR explaining why they
deliberately do not.

**Performance claims come from benchmarks.** A pull request that says "faster" includes the
BenchmarkDotNet table, the hardware it ran on, and the baseline it is compared to. A change that makes a
number worse is fine if it buys correctness; say so.

**No code from MediatR 13 or later.** Those versions are RPL-1.5 or commercially licensed. Do not copy,
translate or paraphrase their source here. See [ADR 0006](docs/adr/0006-clean-room.md).

**Dependencies stay permissive at every depth.** The licence audit runs in CI and fails on anything with a
commercial, subscription or maintenance-fee term.

**Explain the why, not the what.** Comments exist where a reader would reasonably ask why it is done this
way. Decisions with a real trade-off get a short ADR in `docs/adr/`, including the option not taken.

## Commits and pull requests

Conventional commit subjects, one concern per commit, a body when the reason is not obvious from the diff.
Pull requests say what changed, why, and how you know it works.
