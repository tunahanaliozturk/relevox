# 20. The validation behaviour is closed at build time over the requests with a validator

Status: accepted
Date: 2026-10-25

## Context

Validation is the first behaviour almost every MediatR application writes. Clean Architecture's template and
eShop both have one: an open `ValidationBehaviour<,>` that takes `IEnumerable<IValidator<TRequest>>`, runs them,
and throws. Registered open, it runs for every request: the container closes it, resolves an empty list of
validators for most requests, and the behaviour awaits nothing and calls `next`. #54 asked for a package with a
ready behaviour, and for a request with no validator to get no behaviour at all, decided at build time.

The behaviour has to keep three things the applications rely on: where it sits among the other behaviours,
which they control by registration order; the exception it throws, which their API turns into a response; and
the validators they already register with FluentValidation's `AddValidatorsFromAssembly`.

## Decision

**A package, `Relevox.FluentValidation`, with `ValidationBehavior<TRequest, TResponse>` and `options.AddValidation()`.**
`AddValidation` adds the behaviour open to the options' behaviours, in the place it is called, with the lifetime
it is given, exactly as `AddOpenBehavior` would. Validators run one after another, so validators sharing a
scoped `DbContext` never use it at once, and their failures are thrown together.

**The generated `AddRelevox` closes it.** The generator looks for the package's behaviour, and for open classes
derived from it, and for classes implementing FluentValidation's `IValidator<T>` in the project and the projects
it references, packages excluded, as for generic handlers. For each request with a validator for its exact type,
it closes each behaviour over the request and its response where the constraints allow. When the options' list
holds one of those behaviours open, the generated code registers the closings in its place, with its lifetime,
and nothing else: a request without a validator gets no behaviour, and its send takes the direct path.

Registered any other way, with the container directly, the behaviour still works open: the container closes
it for every request, and it runs whatever validators each one has, none for most.

**The exception is FluentValidation's `ValidationException` by default; an application with its own derives.**
A virtual `CreateException` takes the failures. A class derived from the behaviour, passed to
`AddValidation(typeof(Validation<,>))`, is recognised by the generator like the behaviour itself, so Clean
Architecture keeps its `ValidationException` and its API's problem details.

**A generic validator leaves the behaviour open.** A class such as `RequiredFields<T> : AbstractValidator<T>` is
registered by hand, closed over types the build cannot know, generic requests among them. Rather than guess
which requests it covers and risk skipping one, the generated code then leaves the behaviour open for every
request, as the application had it before. FluentValidation's own `InlineValidator<T>` does not count.

**Exact types only.** The container resolves `IValidator<TRequest>` without variance, so a validator for a base
type never ran for a derived request in the behaviours this replaces either; closing over base types would make
the package run validators the application never ran.

## Consequences

- A request without a validator costs what it did before the package. `ValidationBenchmarks` measures it.
- A validator the build cannot see, registered by hand from a package, does not run through `AddValidation`. The
  guide shows registering the behaviour with the container directly for that case.
- The message map reads `AddValidation` and lists the behaviour only under the requests it is closed for.
- The generator knows the package by metadata name only. It references neither FluentValidation nor the package,
  and a project without them generates nothing new.
