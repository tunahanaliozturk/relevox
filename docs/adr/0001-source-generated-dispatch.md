# 1. Source-generated dispatch

Status: accepted
Date: 2026-10-16

## Context

MediatR finds a handler at run time. It scans assemblies at startup, then on each call asks the container
for `IRequestHandler<TRequest, TResponse>` as an open generic through a cached wrapper. That works, costs
about 52 ns and 272 B per call on the hardware fastcast measured, and cannot be trimmed or compiled
ahead of time without the linker keeping every handler alive by hand.

## Decision

Find handlers at compile time with a Roslyn incremental generator, and emit the dispatch code: one typed
`Send` overload per request, a small dispatcher per request for the interface path, and a registration
method that adds every handler by its concrete type.

## Consequences

- A call site that knows its request type binds to a generated overload. There is nothing to look up.
- Missing and duplicate handlers become build diagnostics instead of a runtime exception on the day that
  code path first runs.
- No reflection on the hot path, so trimming and NativeAOT work without annotations.
- The generator is now part of the user's build. It has to be incremental, fast and deterministic, and it
  has to be tested for those properties directly, since the generated output looks right either way.
- Handlers the generator cannot see, such as types built by reflection or open generics closed at run
  time, are not supported. That is a real loss for some codebases and is listed as a limitation.

## Alternatives considered

- **Reflection with caching, as MediatR does.** Known quantity, but it is the thing being replaced, and it
  rules out NativeAOT.
- **Compiled expression trees.** Faster than reflection after warm-up, still runtime discovery, and
  expression compilation is not available under NativeAOT.
