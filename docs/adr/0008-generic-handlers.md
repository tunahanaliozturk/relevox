# 8. Generic handlers are closed at build time

Status: accepted
Date: 2026-10-18

## Context

Until now every generic handler was reported as CPR0004 and left out, on the reasoning that MediatR closes
them at run time and a generator cannot. Probing MediatR 12.5.0 before changing that showed the picture was
wrong in one place that matters:

- **Generic notification handlers are registered by default.** `Audit<T> : INotificationHandler<T>` is added
  to the container as an open generic, and the container closes it for every notification published, within
  its constraints, including notifications nothing else handles. It runs after every handler the scan
  registered, catch-all ones included. Çapar was silently skipping these: a behaviour difference, not a
  missing extra.
- **Generic request and stream handlers are not registered by default.** Sending `GetById<Customer>` to
  `GetByIdHandler<T>` fails for want of a handler unless `RegisterGenericHandlers` is set. With it, MediatR
  closes the handler over every type in the scanned assemblies that satisfies the constraints, compiler
  generated types included, and a generic stream handler makes registration throw.

## Decision

Generic handlers are closed by the generator, and the closings go through the same discovery as written
handlers, so they are held to the same rules.

- A notification handler declared as `H<T> : INotificationHandler<T>` is closed over every notification type
  the build can see whose type satisfies its constraints, and runs after every other handler for that
  notification. A closing does not make the notification count as handled for the catch-all rule, because
  MediatR's scan decides that before the container closes anything.
- A request or stream handler is closed over the types that satisfy its constraints, from this compilation
  and the references that can see the constraint's assembly. Every type parameter needs a constraint naming a
  type declared in the solution; a type from .NET, recognised by Microsoft's strong-name keys, does not count,
  because everything implements `IComparable<T>`. More than 256 closings for one handler is reported.
- A handler written for one closed request wins over the generic one, without CPR0002.
- Anything else is reported as CPR0004, with the reason.

## Consequences

- Generic notification handlers behave as they do under MediatR's default, which the differential tests now
  hold Çapar to, ordering included.
- Generic request handlers work without `RegisterGenericHandlers`. The generator cannot read a run-time
  option, and registering them always means a send that failed under MediatR's default now works, which
  nobody depends on. `RegisterGenericHandlers` and its limits are deleted during migration.
- Under `RegisterGenericHandlers`, MediatR's container returns the closing over a written handler for the same
  request. Çapar keeps the written one. That is listed as a deliberate difference.
- A generic notification handler reaches only notification types the build can see. `EntityCreated<Invoice>`
  published from code the build never compiled, and named by no handler, reaches it under MediatR and not
  under Çapar.
- The closing step looks at every type in the compilation, and at the references, on each compilation. Its
  output is value-equal between edits that change nothing it depends on, so nothing downstream reruns; the
  references are walked once each and cached.

## Alternatives considered

- **Keep reporting every generic handler.** Simple, and wrong for notification handlers, which MediatR runs
  by default.
- **Close generic request handlers only over request types constructed in source.** Precise for the project
  being built, and blind to requests sent from libraries, which the build sees only as compiled code.
- **Close unconstrained type parameters over every type in the solution, as MediatR does.** Generates a
  dispatcher per type for a handler that is usually meant for a handful, and MediatR itself needs limits and a
  timeout to survive it.
- **Fall back to `MakeGenericType` at run time.** Would match MediatR's run-time behaviour exactly, and is the
  one thing NativeAOT cannot do.
