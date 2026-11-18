# 26. Singleton handlers are held per container, scoped ones per mediator on first use

Status: accepted
Date: 2026-11-18

## Context

[ADR 0004](0004-resolve-per-call.md), as amended, keeps a handler registered as singleton or scoped after the
first resolve, because the container would return that instance every time anyway. It kept it in a field of the
generated mediator: a flag and a reference for every handler in the assembly, whether a send ever used it.

The mediator is transient by default, as MediatR's is. An application that resolves `ISender` once per request
therefore builds a mediator per request, and every one of them carried those fields and asked the container again
for each singleton it used. The rows for that pattern (#164) showed it: create a scope, resolve `ISender`, send
once, dispose, at 2,500 request types, allocated 22,696 bytes, against 288 for MediatR and 256 for Mediator.
Every benchmark before #164 held one mediator for the whole run, so none of this had been visible.

Two facts from the container decide where the instances can go instead. A root provider and its scopes share
singletons, so a singleton is one instance per container whichever scope asks. And a scoped instance is per scope,
so it can only be kept by something that lives no longer than the scope, which a mediator resolved from that
scope does.

## Decision

**Singletons are held once per container.** The generator emits a nested `RelevoxMediator.HeldHandlers` class with
a field per handler, and `AddRelevox` registers it as a singleton. It also carries each handler's registered
lifetime and the container's `PipelineState`, so building a mediator is one container lookup, for
`HeldHandlers`, where it was one for `PipelineState`. The first send that needs a singleton handler resolves it
and stores it there; every mediator the container builds after that finds it.

**Scoped handlers are held by the mediator, in storage it creates on first use.** The storage is pages of
handler references, made as they are needed, with the page size chosen at build time near the square root of the
handler count. A mediator that never uses a scoped handler allocates none of it. One that does allocates a page
table and one page, which at 2,500 handlers is 40 references and 64 references, not 2,500.

**Transient handlers are resolved on every send**, as before. So is a handler with no registration, which the
container then reports missing.

**The mediator itself has a fixed size**: the provider, the pipeline state, the held handlers, the scoped storage,
the publisher and two flags, whatever the number of handlers.

**What "the registration in force when the first mediator is built" now means.** The lifetimes are still read from
the service collection, from the last registration of each handler, but once per container, when that container
builds its first mediator. Before, they were read once per `AddRelevox` call and shared by every container built
from the collection. Building two containers from one collection with registrations changed in between now gives
each container the lifetimes it was built with, as far as the collection still shows them.

**Behaviours are not held.** [ADR 0004](0004-resolve-per-call.md)'s first amendment said singleton behaviours were
resolved once and kept; the code never did that, and this decision keeps the code and corrects the record. Every
send with behaviours asks the container for them. Measured with a throwaway benchmark class that called
`GetServices<IPipelineBehavior<Piped, int>>()` from a scope on the containers the pipeline rows use: with three
singleton behaviours the container returned the same array on every call (checked by reference) and the call
took 7.7 ns and allocated nothing; with three transient ones, 21.0 ns and 120 bytes. Holding the singleton case
would save those 7.7 ns of a pipeline send that took 53 to 75 ns in the timing rounds, and no bytes. It would also
need every behaviour descriptor for the request type, open generic ones included, to be read and judged, and it
would stop a container whose scopes add behaviours of their own, which `PipelineState` checks for on every send,
from running them. That does not pay.

## Consequences

- The scope-per-send row allocates 216 bytes at 100, 500 and 2,500 request types alike, from 1,096, 4,696 and
  22,696 before, and at 2,500 it took 105 to 125 ns in three rounds where it took 1,781 to 2,536. The row in the
  main benchmarks, with singleton handlers, went from 264 to 192 bytes. The numbers and their conditions are in
  [2026-11-18-held-handlers.md](../benchmark-results/2026-11-18-held-handlers.md).
- Every row that holds one mediator for the run allocates exactly what it did, and none is clearly slower. A held
  singleton is still a field read on the send path, now through `HeldHandlers`; a transient handler reads its
  lifetime from an array where it read a flag, and is resolved in place with its type known. Sending transient
  handlers through the shared generic method that holds the others was tried first and made a five-handler publish
  about a fifth slower.
- Two containers never share a handler instance, including two built from one collection: `HeldHandlers` is a
  singleton of each.
- A container whose scopes can register a singleton handler again, as some third-party containers allow, now gets
  the instance the first mediator resolved in every scope. Under MS.DI, and under any container whose scopes share
  the root's singletons, that is the instance the container would have returned anyway. A handler that has to
  differ per scope should be registered as scoped.
- `RelevoxMediator`'s constructor takes `HeldHandlers` instead of the keep flags and the pipeline state. It is
  generated code, compiled with the generator that calls it, so nothing in `Relevox.Internal` changes
  ([ADR 0011](0011-internal-api-is-a-contract.md)).

## Alternatives considered

- **An array per container in `PipelineState`.** One fewer generated type, but `PipelineState` is a contract
  ([ADR 0011](0011-internal-api-is-a-contract.md)) and would grow a constructor and a member for it, and every
  send of a singleton would pay a cast from `object`. A generated class has a typed field per handler.
- **A flat array of scoped handlers, created on first use.** The simplest storage, and at 2,500 handlers a
  20,000-byte allocation for the first scoped handler a scope uses, which is most of what this decision removes.
- **A hash table keyed by handler index for scoped handlers.** Smallest when few handlers are used, but a mediator
  can be used from several threads at once, and a table that is written in place needs locking or copying on
  every insert. Pages of references are written one slot at a time, which is safe without either: a slot only
  ever holds its own handler, and a lost write only means a second resolve, which the scope answers with the same
  instance.
- **Not holding scoped handlers at all.** The scope already caches them, and for one send per scope it would be
  the same. A scope that sends many times, or a scoped mediator in a long-lived scope, would ask the container on
  every send again, which ADR 0004's second amendment removed on purpose.
- **Holding singleton behaviours.** See above: no bytes saved, 7.7 ns at most, and a scope's own behaviours lost.
