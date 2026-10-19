# 11. Relevox.Internal is a contract between generated code and the runtime

Status: accepted
Date: 2026-10-19

## Context

The generated mediator calls public types in `Relevox.Internal`: `PipelineState`, `RequestPipeline`,
`ExceptionCatalog`, the built-in slots, `DeferredEnumerable`, `OpenNotificationHandlers`. They are public because
generated code in another assembly has to call them, and hidden with `EditorBrowsable(Never)` because nobody else
should.

Hiding them does not make them private. A library with a mediator of its own is compiled against the `Relevox`
version it referenced, and then runs inside an application that may bring a newer one. If a signature in
`Relevox.Internal` changed in between, the library's generated code would throw `MissingMethodException` the
first time it ran.

## Decision

Within a major version, `Relevox.Internal` only grows:

- types and members are added;
- none are removed;
- no signatures change;
- no behaviour that generated code relies on changes.

The same rule covers the public API. Once a version is on NuGet, the build checks it with package validation,
using the last release as the baseline. That compares every public member, `Relevox.Internal` included. A change that has to break it waits
for the next major version. In the meantime the generator emits calls to the new shape and keeps the old one
working.

## Consequences

- A library built against 1.0 keeps working in an application on any 1.x.
- `Relevox.Internal` costs as much care as the API users see, and the hidden types are not a free area for
  refactoring.

## Alternatives considered

- **Version the entry points**, as in `PipelineStateV1` and `PipelineStateV2` side by side. The rule above
  needs this anyway, at the moment something has to break. Starting with it would put a version number on
  every type before there is a reason to.
- **Generate everything into each assembly**, with no shared runtime types. That would mean no binary contract
  at all, but every library would carry its own copy of the pipeline. Every copy would need fixing for a bug,
  and the singleton slots (`BuiltInSlot`) that one container shares would no longer be shared.
