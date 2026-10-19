# 9. Generic notification handlers also close at run time

Status: accepted
Date: 2026-10-19

## Context

[ADR 0008](0008-generic-handlers.md) closes `Audit<T> : INotificationHandler<T>` at build time, over every
notification type the generator can see. It left one gap and named it: a notification type no declaration
names never reaches the generic handler. Under MediatR it does, because MediatR registers the handler open
and the container closes it for whatever is published.

The gap is not exotic. A generic base entity that raises `new EntityCreated<TSelf>(this)` builds
`EntityCreated<Invoice>` without any line of code naming it. The events are then published one by one through
a base type: `INotification` in eShop, an abstract `BaseEvent` in the Clean Architecture template. Neither
trial has a generic event, but both have the shape that would carry one. An audit or outbox handler written
as `H<T> : INotificationHandler<T>` would stop seeing those events after the migration, with no warning,
since nothing at build time knows they exist.

## Decision

The build-time closings stay as they are, and each generic notification handler is also kept open:

- The generator registers it with the container as an open generic, with the handler lifetime, as MediatR's
  scan does.
- The generated mediator passes the open definitions, in the order MediatR would run them, to
  `Capar.Internal.OpenNotificationHandlers`.
- A published notification whose type has no entry in the generated mediator goes there. It closes each
  definition over the runtime type with `MakeGenericType`, keeps the ones whose constraints the type satisfies,
  and remembers the result per type. It resolves the handlers from the container and runs them, one after
  another or through the configured publisher, the same way it runs the handlers the build knew about.

A type the generated mediator has an entry for never takes this path: its closings were written at build time,
along with the entry. Every type without one does, including a type the build saw but no handler names, which
usually finds nothing and is remembered as having nothing.

A generic handler whose type parameter carries `[DynamicallyAccessedMembers]` is left off the list. A type
closed at run time cannot promise the trimmer kept those members.

## Consequences

- A generic notification handler reaches every notification type its constraints admit, as under MediatR,
  within what Çapar registers at all: not an internal handler in another project (CPR0003), nor a handler in
  a NuGet package, which MediatR's scan of the application's assemblies would not find either. The
  differential tests hold this for a closed generic notification built from a type parameter, published
  as `INotification`, as `object`, and under `TaskWhenAllPublisher`, and for a struct its constraints rule
  out.
- This is the one place Çapar closes a type at run time. The cost is paid once per notification type: an
  exception for each definition whose constraints fail, then a dictionary lookup. Nothing changes for a
  project with no generic notification handler; its generated code is the same as before.
- Under NativeAOT a closing over a reference type works, and the AOT sample in CI checks two: a handler
  the build also closed over other notifications, and one nothing at build time closed. A closing over a
  struct needs code generated at run time, so under NativeAOT it is skipped, which leaves such a struct where
  ADR 0008 left every unseen type. Microsoft's container refuses open generics over structs under NativeAOT
  for the same reason. Should the compiler still have no code for a closing, publishing that type throws a
  `NotSupportedException` that says which handler and how to fix it, rather than quietly running fewer
  handlers.
- The closings are kept in a static cache keyed by type, which holds on to those types. An application that
  unloads plugins through a collectible `AssemblyLoadContext` and publishes their notification types through
  this path will keep those contexts alive.
- Catch-all handlers, written for a base type or for `INotification`, still do not run for an unseen type.
  That is MediatR's behaviour too. Its scan registers them only under the notification types it found, and
  Microsoft's container does not apply variance.

## Alternatives considered

- **Close over generic types constructed in source.** The generator would look for `new EntityCreated<Invoice>()`
  and close over what it finds. It would miss the case that matters: inside a generic base class, the
  constructed type is `EntityCreated<TSelf>`, and finding out what `TSelf` becomes means following the type
  through every subclass. It would also miss types built in another assembly.
- **Leave it, and document it.** That was ADR 0008's position. It stops being acceptable once both real
  migrations turn out to publish domain events through a base type, which is exactly the path that hides
  the closed type from the build. A silent change in which handlers run is the worst kind of migration
  difference.
- **Resolve `IEnumerable<INotificationHandler<T>>` from the container for unseen types.** This needs the
  closed interface type at run time, which is `MakeGenericType` all the same. It would also pick up every
  handler registered for that interface, not only the generic ones, and so would differ from what the
  generated entries do for the types the build saw.
