# 6. Clean-room rule, and MediatR 12.5.0 as the test oracle

Status: accepted
Date: 2026-10-16

## Context

MediatR 13 and later are licensed under the Reciprocal Public License 1.5 or a commercial agreement. Code
derived from them would carry those terms into this MIT library. MediatR 12.5.0 and its contracts package
are Apache-2.0.

Before planning, the public surface of 12.5.0 and 14.2.0 was compared with REA's managed-code inspection,
which reads metadata without loading or running the assembly. Both expose the same 38 public types; the
only public change is a renamed DI extension class. 14.x adds an internal `MediatR.Licensing` namespace
and a dependency on `Microsoft.IdentityModel.JsonWebTokens`. The paid line added licensing, not API.

## Decision

- No source from MediatR 13 or later is read, copied, translated or paraphrased here. Its binary was
  inspected for public type names only, to confirm the API had not moved.
- The 12.5.0 public API shape is matched on purpose, because that shape is what migrating code is written
  against. API shape is not code.
- Çapar is written from the behaviour described in the plan and pinned down by tests. Its source is not
  ported from 12.5.0 either.
- MediatR 12.5.0 is used as a test-only dependency: differential tests run the same scenario on both
  libraries and require identical traces of which handler, behaviour and processor ran, in what order,
  with what exception.

## Consequences

- "Behaves like MediatR" is a test result, not a claim.
- Where MediatR 12.5.0 has a bug, the differential test will insist on reproducing it. Each deliberate
  divergence gets its own ADR and an explicit exception in the test.
