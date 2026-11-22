# 28. An options object passed to AddMediatR becomes a lambda

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
lambda. The parameter takes the local's name, so the moved statements need no change; for an object made in the
call it is `options`.

**Statements move past the ones between them and the call, with a check.** The moved statements run when
`AddRelevox` calls the lambda, later than they ran before. That matters only if something in between changes what
they read, so the fix asks the compiler's data flow analysis: when a statement in between writes a variable the
moved code reads, nothing is offered. The alternative, refusing whenever anything stands between the local and the
call, would refuse the common case of another registration in between.

**Anything else is left for a person, and keeps its compiler error.** That covers a local used anywhere but as the
receiver of a plain statement and as the argument (passed to a method, stored, read after the call), a statement
inside an `if` or a loop, a local declared outside the block, a call made inside a lambda, which would run the
statements later still, a moved statement that declares a variable, and any preprocessor directive between the
declaration and the call. Disabled text under a directive is not code the compiler sees, so a use of the local
inside it could not be found and moved.

**The analyzer leaves the MediatR names inside such an object to this rewrite.** "Fix all" merges each fix's text
changes and drops one of two that overlap. Renaming `MediatRServiceConfiguration` in a declaration the rewrite
deletes is such an overlap, so the analyzer does not report names there, and the rewrite moves them itself, with
the same rules (`MediatRUsageAnalyzer.KindOf`). The recognition of the shapes is one method,
`MediatRUsageAnalyzer.OptionsObject`, used by both, so the two cannot disagree about which objects are rewritten.

## Consequences

- The common shapes migrate in the same single "Fix all" as the rest of the code; the tests run one and build the
  result.
- Arguments of the moved statements are evaluated later than before. A side effect in them, such as a method call
  that logs, happens at the `AddRelevox` call. Configuration code rarely has one, and the data flow check covers the
  case that changes a value.
- An options object built in another method is still left for a person (#62 scoped it out): following it there
  means proving that nothing else uses it.
