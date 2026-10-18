# 5. No `namespace MediatR` shim

Status: accepted
Date: 2026-10-16

## Context

The shortest possible migration would be a package that declares its types in `namespace MediatR`, so that
swapping the package reference is the only change. Several people have asked other libraries for exactly
that.

## Decision

Çapar's types live in `Capar`. Migration means changing the package reference and the `using` directives.
A code fix in the `Capar` package rewrites the directives and the registration call (added before the first
release, as `Capar.CodeFixes`).

## Consequences

- One extra mechanical step for the user, done once with the code fix, or with find and replace outside an
  IDE.
- A project can reference MediatR and Çapar at the same time during a gradual migration, which a shim
  would make impossible: the two would declare the same type names in the same namespace.
- Nobody reading a stack trace has to wonder which library they are actually running.

## Alternatives considered

- **The shim.** Convenient, and it would put another project's name on our types, conflict with the real
  package, and make every bug report ambiguous.
