## What and why

<!-- What changes, and the reason. Link the issue if there is one. -->

## How you know it works

<!-- The test that would fail without this change. For a change in behaviour, the differential scenario that
     shows Çapar and MediatR 12.5 agree, or the ADR that says why they deliberately do not. For a performance
     claim, the BenchmarkDotNet table and the machine it ran on. -->

## Checklist

- [ ] `dotnet build Capar.slnx` with no warnings
- [ ] Generator, differential and web sample tests pass
- [ ] `bash eng/package-smoke.sh` passes, if anything that ships in a package changed
- [ ] CHANGELOG.md updated under Unreleased
