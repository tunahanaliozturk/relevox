# 22. MediatR and Relevox side by side, routed by the message's interface

Status: accepted
Date: 2026-10-29

## Context

Moving to Relevox is one change: swap the packages, run the code fix, build. A solution with hundreds of
handlers and a team working in it gets that as one pull request nobody wants to review, so the move does not
happen. #116 asks for MediatR and Relevox to run together while a team moves one feature at a time, and for code
that injects MediatR's `IMediator`, `ISender` or `IPublisher` to keep working throughout.

Three things have to be decided: how the bridge knows which library a message belongs to, what a notification
with handlers in both does, and which MediatR versions the bridge accepts under which licences.

## Decision

**A package, `Relevox.MediatRBridge`, with `services.AddMediatRBridge()`.** It registers its own implementation
of MediatR's `IMediator`, `ISender` and `IPublisher`. The mediator `AddMediatR` registered is kept under a
private key and called for whatever has not moved; Relevox's comes from the generated `AddRelevox`. MediatR adds
its interfaces with `TryAdd`, so the calls can come in any order. A custom `MediatorImplementationType` is kept
when `AddMediatR` comes first; after the bridge, MediatR's own `Mediator` is used.

**A message has moved when its type implements Relevox's interface.** A moved request implements both, such as
`record PlaceOrder : MediatR.IRequest<int>, Relevox.IRequest<int>`, so a caller on MediatR's `ISender` still
compiles, and its handler implements Relevox's `IRequestHandler`. The bridge tests the request with `is`: a
Relevox request goes to Relevox, anything else to MediatR. The same holds for void requests, stream requests
and the untyped overloads. A void request sent untyped answers with MediatR's `Unit`, as MediatR would, because
the caller is on MediatR's interface.

The rule needs no list of handlers at run time, and the compiler checks it: RVX0001 warns at build time about a
Relevox request with no Relevox handler, and with warnings as errors the build stops there. A request whose
handler is still on MediatR is reported before the bridge could send it to the wrong library.

**Each request runs one pipeline.** It goes to one mediator and through that mediator's behaviours,
processors and exception handlers only. A behaviour moved by implementing both libraries' interfaces, and
registered with both, therefore runs once per request. A behaviour left on MediatR does not run for requests
that have moved; #132 adds a diagnostic for that.

**A notification implementing both interfaces runs MediatR's handlers, then Relevox's.** One that implements only
one goes only there. MediatR's handlers come first because they were there first, and a handler moving changes
only its own place, to the end. With MediatR's default publisher a handler that throws stops the rest, so it
stops the Relevox side too; with a parallel publisher on either side, that side runs its own handlers in
parallel and the sides still run one after the other.

**MediatR 12.0.0 and later.** 12.0 is the first version with the interfaces the bridge implements, and the
package depends on it as a minimum, so an application keeps the MediatR version it chose. The 12.x releases are
Apache-2.0. 13 and later are RPL-1.5 or commercial and read a licence key at run time: an application on them
brings that version and its licence itself, and the bridge only calls the interfaces. Nothing from MediatR 13 or
later is in this repository, as ADR 0006 requires. The licence audit runs over the package with MediatR 12.0.0
resolved, and the tests run against 12.5.0. Versions 13 and 14 have the same public interfaces as 12.5; the
real-solution trial in #133 runs the bridge on one of them.

## Consequences

- A team can move a feature at a time and stop. Callers keep MediatR's interfaces until the end.
- A moved request carries two interfaces until MediatR is removed, and the code fix that moves a file has to
  leave MediatR's in place while the bridge is used.
- An application moving to NativeAOT gets nothing from the bridge: MediatR finds its handlers by reflection.
- Code that has moved to Relevox's `ISender` cannot send a request still on MediatR. Callers move after their
  messages, not before.

## Alternatives considered

- **Route by asking the generated mediator which types it handles.** The answer would be exact even for a
  request typed for Relevox whose handler is still on MediatR, but it needs a new member on the generated
  mediator and in `Relevox.Internal` (ADR 0011), and that case is already a build error. The interface test is
  the same answer, decided earlier and for free.
- **Try Relevox first and fall back to MediatR on a missing handler.** Every MediatR send would pay for an
  exception, and a real "no handler" error would be hidden.
- **Relevox's own interfaces as the bridge, routing to MediatR.** Callers would have to change first, which is
  the big pull request this is meant to avoid.
- **`options.BridgeMediatR()` on `AddRelevox`'s options,** as #116 first suggested. The options cannot reach the
  service collection, and the generated method would need to know about a package it does not reference. A
  service collection extension needs neither.
