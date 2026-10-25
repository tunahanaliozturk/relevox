# 19. Behaviours can be ordered by number, worked out on the first send

Status: accepted
Date: 2026-10-25

## Context

In MediatR, and in Relevox until 1.5, pipeline behaviours run in the order the container returns them, which
is registration order. That order is spread across an `AddMediatR` options lambda, plain `AddTransient` calls
before and after it, and sometimes extension methods in other projects. Nothing points out when it changes: a
validation behaviour that ends up inside a transaction behaviour still compiles, still runs, and now opens a
transaction for every invalid request.

#54 asked for an opt-in way to say where a behaviour runs, with the order staying exactly as MediatR's for
everyone who does not use it. Three places could apply such an order:

- **The service collection, inside `AddRelevox`.** Sorting the registrations there misses everything
  registered after the call, and rearranges the application's own collection behind its back.
- **The generated code.** The generator sees the behaviour classes in the compilation and the ones the options
  lambda adds, but not the container: registrations made directly, in other projects, or from a type in a
  variable are invisible to it, and those are the ones that run.
- **The first send of each request type.** The mediator already asks the container for the behaviours on that
  send and learns what the built-in behaviours do (`PipelineState`). Every registration is visible there, made
  anywhere, and the work is done once per request type.

## Decision

**`[PipelineOrder(n)]` on a behaviour class**, pipeline or stream, in `Relevox.Contracts`. A lower number runs
further out. A behaviour without it counts as 0, and so do the built-in processor and exception behaviours.
Behaviours with the same number keep registration order, so without the attribute nothing moves. The attribute
is inherited, so a behaviour derived from an ordered one keeps its place. Default 0 and lower-is-outer are the
convention of ASP.NET Core's filter `Order`, which most .NET developers have met.

**The order is applied on the first send of a request type**, together with the built-in slots: the
behaviours are put in order first, and the slots are judged in that order, since whether an idle one can be
left out depends on what runs outside it. What is learned is where each running position comes from, plus the
behaviour types it was learned from.

**A scope may return another list.** A child scope in Autofac can register behaviours of its own, so one
request type can come back with other behaviours than its first send saw, in another order or with an ordered
one added. A list learned with an ordered behaviour in it, in place or not, keeps its types, and each later send
compares them and works the order out again, without remembering, when they differ. A list learned with no
ordered behaviour keeps only its length, which each later send compares: one more comparison per send for
every application, against a type check per behaviour that only applications using the attribute pay. A scope
that swaps a behaviour for an ordered one, keeping the length, is the case left out.

**The container's array is never changed.** For behaviours that are all singletons the container hands out the
same array on every call, so sorting it in place would scramble the next send. A request type whose behaviours
the attribute moves gets a new array per send. One whose behaviours are already in order runs from the
container's array, as before. `PipelineOrderBenchmarks` measures the three cases.

**Built-ins count as 0, so a negative number runs outside them.** Registered by `AddRelevox`, the exception
and processor behaviours usually sit before the options' behaviours, so a behaviour at -1 runs outside
exception handling and the pre-processors, and one at 0 or above inside them, as before. This is the only way
to put a behaviour of the application's outside the built-ins, and the message map shows it: the Markdown view
lists such a behaviour above the pre-processors, and the JSON gives each ordered behaviour its `order`.

Processors are not ordered by the attribute. They run inside their own behaviour, in registration order, as
in MediatR.

## Consequences

- The differential tests, which register no attribute, pin that MediatR's order is unchanged; the order
  itself is pinned by `PipelineOrderTests`.
- A behaviour moved by the attribute costs one small array per send, plus a type check per behaviour. An
  application that wants no array registers its ordered behaviours in their order.
- Two behaviours with one number fall back to registration order, the thing the attribute is meant to remove.
  #98 adds a diagnostic for it.
- Reading the attribute is reflection on the behaviour's runtime type, once per request type. It needs no
  annotation for trimming or NativeAOT.
