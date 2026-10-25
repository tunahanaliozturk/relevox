# Relevox.FluentValidation

[GitHub](https://github.com/tunahanaliozturk/relevox) ·
[Validation guide](https://github.com/tunahanaliozturk/relevox/blob/main/docs/validation.md) ·
[Changelog](https://github.com/tunahanaliozturk/relevox/blob/main/CHANGELOG.md)

A FluentValidation pipeline behaviour for [Relevox](https://github.com/tunahanaliozturk/relevox), the
source-generated mediator with MediatR 12's handler signatures. It runs the validators registered for a request
before its handler and throws if any fails. The build gives it only to the requests it found a validator for,
so a request without one pays nothing for it.

```bash
dotnet add package Relevox.FluentValidation
```

```csharp
services.AddValidatorsFromAssembly(typeof(PlaceOrder).Assembly);
services.AddRelevox(options => options.AddValidation());
```

FluentValidation's `ValidationException` is thrown with every failure. To throw an exception of your own, derive
from `ValidationBehavior<TRequest, TResponse>`, override `CreateException`, and pass the class to
`AddValidation(typeof(YourValidation<,>))`.
