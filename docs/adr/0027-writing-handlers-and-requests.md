# 27. Writing a missing handler or request

Status: accepted
Date: 2026-11-22

## Context

RVX0001 says a request has no handler and leaves the writing to the person. For someone new to Relevox that is
the first warning they meet, and the handler it asks for always has the same shape: the interface that matches
the request, a `Handle` method with MediatR 12's signature (`Task`, never `ValueTask`, [ADR 0023](0023-no-valuetask-handlers.md)),
and a body to fill in. #173 asks for a code fix that writes it, and for refactorings that start from the other
end: a plain record that should be a request, and a handler whose request does not exist yet.

Three facts shape the answer.

- RVX0001 is reported by `RelevoxAnalyzer`, not by the generator, at the request's declaration in source, so a
  code fix can attach to it. It is a compilation-end diagnostic: whether a request has a handler is only known
  once every type has been seen. The IDE computes such diagnostics when it analyses the whole project, not as
  you type in one file, so with analysis scoped to open documents the fix may not be offered where the warning
  is listed from a build.
- No other rule reports a missing handler. A notification with no handler is legal, as in MediatR, and
  RVX0002 to RVX0006 are about handlers that exist.
- The code fixes ship in `Relevox.CodeFixes.dll`, under `analyzers/` in `Relevox.Contracts`, which every project
  with RVX0001 references through `Relevox`. A new provider in that assembly is picked up without packing changes.

## Decision

**A code fix for RVX0001, and a refactoring provider with three actions, all in `Relevox.CodeFixes`.**

- The fix, **Add handler 'PingHandler'**, writes a `sealed class` in a new file in the request's folder and
  namespace. It is `public` when the request is, otherwise `internal`. The body throws `NotImplementedException`.
  Names are written in full and then shortened by the workspace, which adds a using only when the name is not
  already in scope, global usings included. The namespace is file-scoped when the request's file uses one. A
  file or type name already taken gets a number: `PingHandler2`.
- The same action is a refactoring on a request's declaration when no type in the project handles it, so it is
  there as you type, whatever RVX0001's timing.
- **Make 'Ping' a request**, alone or with its handler, on a class, record or struct that is not a message.
  It adds `IRequest`, with no response, because nothing here can know the response type.
- **Add request 'Ping'** on a handler whose base list names a request, stream request or notification that does
  not resolve. It writes a `sealed record` with the matching interface in a new file next to the handler.

**No fix-all.** The fix returns no `FixAllProvider`, and `dotnet format` only applies fixes that have one.
Fixing every RVX0001 in a solution at once would replace a build warning about each missing handler with a
handler that throws when the request is sent, which is the failure the rule exists to move to the build.

**Deliberately refused, each with a test:** generic requests and requests nested in generic types, which the
generator reaches through their closed forms ([ADR 0008](0008-generic-handlers.md)), so a generic handler is a
design choice; requests nested in a `private` or `protected` type, which a top-level handler cannot name;
abstract and static types; a missing request named other than by a plain identifier (`Orders.Place`,
`Page<int>`), where the name says more about where the type belongs than a template can honour.

## Consequences

- With the warning and the refactoring both live, the light bulb can list **Add handler** twice, once as a fix
  and once as a refactoring. Both write the same file.
- The refactoring looks for handlers in the project only. A handler in a project that references this one is not
  seen, so the action is offered where RVX0001 is not reported; applying it then gives RVX0002 in the project that
  has both handlers.
- A request nested in a public type gets a top-level handler named after the request alone (`PlaceHandler` for
  `Orders.Place`), which names the request through its container.

## Alternatives considered

- **A fix-all that writes every missing handler.** Useful after a large move, and cheap to write with a custom
  fix-all provider, since the batch fixer cannot add documents. Rejected for the reason above; one handler at a
  time keeps the person looking at each.
- **A second analyzer that reports RVX0001 per file, so the fix shows as you type.** It would have to repeat
  the generator's discovery over the whole compilation for each file, or guess without it. The refactoring gives
  the same action without reporting anything.
- **Writing the handler into the request's file.** Fewer files, but handlers usually live in a file of their
  own, and moving one out later is more work than deleting a file.
- **Asking for the response type when making a type a request.** Roslyn's refactorings have no prompt. `IRequest`
  without a response is a valid request; changing it to `IRequest<T>` is one edit, and the handler fix then
  writes the matching signature.
