# Validation with FluentValidation

`Relevox.FluentValidation` replaces the `ValidationBehaviour<,>` most MediatR applications copy from a template.
It runs every FluentValidation validator registered for a request before the handler, and throws if any of them
fails. The build gives the behaviour only to the requests it found a validator for, so a request without one
pays nothing for it ([ADR 0020](adr/0020-validation-package.md)).

```bash
dotnet add package Relevox.FluentValidation
```

Reference it from the project that calls `AddRelevox`.

## Setting it up

```csharp
services.AddValidatorsFromAssembly(typeof(PlaceOrder).Assembly);   // FluentValidation's own registration
services.AddRelevox(options => options
    .AddOpenBehavior(typeof(LoggingBehaviour<,>))
    .AddValidation()                                                // here in the pipeline
    .AddOpenBehavior(typeof(PerformanceBehaviour<,>)));
```

`AddValidation` takes the place in the pipeline where it is called, like `AddOpenBehavior`, and a lifetime, which
is transient by default. Validators are registered as before; `AddValidation` only adds the behaviour.

## What it does

- The validators run one after another, in the order the container returns them, so validators that share a
  scoped `DbContext` never use it at the same time. Each gets the request's `CancellationToken`.
- The failures of every validator are collected, and thrown together in one exception, in the order the
  validators ran. When there are none, the rest of the pipeline runs.
- The exception is FluentValidation's `ValidationException`, with every failure in `Errors`.

## Throwing your own exception

Clean Architecture's template throws its own `ValidationException`, with the failures grouped by property, and
its API turns that into a problem details response. Keep it by deriving from the behaviour:

```csharp
public sealed class Validation<TRequest, TResponse>(IEnumerable<IValidator<TRequest>> validators)
    : ValidationBehavior<TRequest, TResponse>(validators)
    where TRequest : notnull
{
    protected override Exception CreateException(IReadOnlyList<ValidationFailure> failures) =>
        new Application.Common.Exceptions.ValidationException(failures);
}

services.AddRelevox(options => options.AddValidation(typeof(Validation<,>)));
```

The derived class can add constraints of its own, such as `where TRequest : ICommand`; the build then gives it
only to the requests that have a validator and meet them.

## Which requests get it

A request gets the behaviour when the project that calls `AddRelevox`, or a project it references, declares a
class implementing `IValidator<TheRequest>`, usually an `AbstractValidator<TheRequest>`. Projects only reached
through NuGet packages are not looked at, the same rule generic handlers follow.

A generic validator, such as `RequiredFields<T> : AbstractValidator<T> where T : INamed`, is registered by hand,
closed over types the build cannot see, so it counts for every request its constraints allow, and a generic
validator of any other shape for every request: no validator the application registered goes unrun, and a
request it does not cover only resolves an empty list of validators, as it would have without the package.

The validator has to be for the request type itself. The container resolves `IValidator<TRequest>` without
variance, so a validator written for a base class or an interface never runs for a request derived from it,
here or in the behaviour MediatR applications write.

A validator registered by hand for a request the build cannot see a validator for, from a package, for
example, does not run. For that case, register the behaviour with the container directly, for every request:
`services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>))`. Only what goes through
`AddRelevox`'s options is closed at build time, `AddOpenBehavior(typeof(ValidationBehavior<,>))` included.

The [message map](message-map.md) lists the behaviour under each request that gets it.

## Cost

A request without a validator is dispatched exactly as without the package: `ValidationBenchmarks` compares the
two, and a request whose validator passes.
