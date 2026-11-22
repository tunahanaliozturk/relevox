# 29. An options object passed to AddMediatR becomes a lambda

Status: accepted
Date: 2026-11-22

## Context

MediatR 12 has two registration methods: `AddMediatR(Action<MediatRServiceConfiguration>)` and
`AddMediatR(MediatRServiceConfiguration)`. `AddRelevox` takes only the first shape, so until now the RVX0007 code
fix left a call of the second shape as a compiler error for a person (#62).

A search of public code for `new MediatRServiceConfiguration` and `AddMediatR(config)` found these shapes, most
common first:

- a local made with `new`, filled in by statements, then passed: `var config = new MediatRServiceConfiguration();
  config.RegisterServicesFromAssembly(...); services.AddMediatR(config);`, sometimes with an object initializer,
  sometimes with other statements in between;
- the object made in the call: `services.AddMediatR(new MediatRServiceConfiguration().RegisterServicesFromAssembly(...))`;
- a local made from a chain, `var config = new MediatRServiceConfiguration().RegisterServicesFromAssemblies(...);`;
- a parameter, filled in by the caller or by a method it was passed to, then passed on;
- objects passed to `ServiceRegistrar.AddRequiredServices`, MediatR's internals, which have no Relevox counterpart.

In nearly every case the statements only scan assemblies, which the fix drops anyway.

## Decision

**The object made in the call, or a local made in the block that makes the call, becomes the lambda.** Its
initializer's assignments, the calls chained after `new`, and the statements that fill it in each become a
statement on the lambda's parameter, and then the settings Relevox has no use for are dropped as they are from a
lambda. The parameter takes the local's name as written, `@event` included, so the moved statements need no
change; for an object made in the call it is `options`. The static form is handled too, with or without its
namespace: `Microsoft.Extensions.DependencyInjection.MediatRServiceCollectionExtensions.AddMediatR(services, config)`
becomes the same call on `RelevoxServiceCollectionExtensions`.

**Statements move past the ones between them and the call only when neither side can affect the other.** The
moved code runs when `AddRelevox` calls the lambda, later than it ran before. Proving that a statement in between
has no effect on it, or sees none of its effects, would mean proving methods pure, which the fix does not try. It
uses a rule that fits in one sentence instead: a statement may stay in between only if it mentions no variable,
field, property or event that the moved code mentions, and every argument and assigned value in the moved code is a
literal, `default`, a `typeof` or a member of one, a constant or enum member, a lambda, or a read of a local or
parameter. With nothing in between, nothing moves past anything, and the rule does not apply.

A first version compared what the moved code reads with what the statements in between write. It missed a method
in between that changes a static property the moved code reads, and moved code whose own effects a statement in
between would see; the review of #62 found both.

Of the shapes in the search, this still covers the common ones: a local followed directly by the call, with any
arguments, and a local whose statements pass `typeof(...)`, `typeof(Program).Assembly`, enum members such as
`ServiceLifetime.Scoped` or no arguments at all, with other registrations in between. What it refuses is moved code
that calls a method for a value, such as `Assembly.GetExecutingAssembly()`, or creates an object, such as
`new TaskWhenAllPublisher()`, when a statement stands between it and the call.

**Anything else is left for a person, and keeps its compiler error.** That covers a local used anywhere but as the
receiver of a plain statement and as the argument (passed to a method, stored, read after the call), a statement
inside an `if` or a loop, a local declared outside the block, a call made inside a lambda, which would run the
statements later still, an object returned by a method, a moved statement that declares a variable or contains an
`await`, which cannot move into a lambda that is not async, and any preprocessor directive between the declaration
and the call, `#region` included. Disabled text under a directive is not code the compiler sees, so a use of the
local inside it could not be found and moved.

**The analyzer leaves the MediatR names inside such an object to this rewrite.** "Fix all" merges each fix's text
changes and drops one of two that overlap. Renaming `MediatRServiceConfiguration` in a declaration the rewrite
deletes is such an overlap, so the analyzer does not report names there, and the rewrite moves them itself, with
the same rules (`MediatRUsageAnalyzer.KindOf`). The shapes are recognised by one method,
`MediatRUsageAnalyzer.OptionsObject`, used by both. The call around the object can still be one the fix cannot
carry over, for example `AddMediatR` called through a `using` alias of `MediatRServiceCollectionExtensions`. Then
the fix for that call still moves the names inside the object, and leaves the call. So every MediatR name is either
reported or moved by the call's fix; a test runs one "Fix all" and checks that none is left.

## Consequences

- The common shapes migrate in the same single "Fix all" as the rest of the code; the tests run one and build the
  result.
- With nothing in between, arguments of the moved statements are still evaluated a little later than before:
  after the receiver of the call, inside `AddRelevox`. A side effect in them, such as a method that logs, happens
  there. With statements in between, the rule above allows no such argument.
- A comment at the end of the declaration's line stays, on a line of its own above the next statement, since it
  was about the configuration rather than the line.
- An options object built in another method is still left for a person (#62 scoped it out): following it there
  means proving that nothing else uses it.
