# 12. A testing package with a fake mediator and a notification recorder

Status: accepted
Date: 2026-10-20

## Context

Code that uses MediatR is tested by mocking `IMediator`. With Moq or NSubstitute that means picking the right
one of three `Send` overloads and two `Publish` overloads, matching on `It.IsAny<CancellationToken>()`, and
verifying calls rather than reading values. A setup against the wrong overload matches nothing, and a mock
answers an unexpected request with `default`, so a test can pass while checking nothing.

Integration tests have the opposite need: the real handlers should run, and the test wants to know which
notifications went out.

## Decision

Ship `Relevox.Testing` with two things and nothing else:

- **`FakeMediator`**, an `IMediator` written by hand. Setups are keyed by request type and given as lambdas
  whose parameter type names the request, so the compiler infers both type arguments and checks the response
  against the request's `IRequest<T>`. It records sent requests and published notifications as objects. A
  request with no setup fails, naming the type.
- **`NotificationRecorder`**, registered by `AddRelevoxNotificationRecorder`, which replaces the
  `INotificationPublisher` in force with one that records and then delegates to it.

The package depends on `Relevox` for `INotificationPublisher`, with its analyzers excluded, so the source
generator does not run in a test project through this package.

## Consequences

- A test project that references the application and `Relevox.Testing` still has one mediator: the
  application's.
- The recorder takes the publisher off the generated mediator's inline sequential path, so a publish in a test
  with the recorder goes through the general path instead. It behaves the same; it is not what the benchmarks
  measure.
- The fake knows nothing about behaviours or processors. A test that needs them uses the real mediator.

## Alternatives considered

- **A fake generated per project**, with typed setup methods per request. Discoverable in the IDE, but it puts
  the generator in the test project, which is the thing this package avoids, and the lambda form already gets
  the compiler to check types.
- **Assertion helpers for Shouldly or FluentAssertions.** `Sent<T>()` and `Published<T>()` return plain lists,
  which every assertion library already handles. One integration per library is upkeep with nothing gained.
- **A recording `INotificationHandler<T>`** registered open in the container. The generated mediator dispatches
  to the handlers it found at build time, so a handler added at run time would see only notification types the
  build never saw. The publisher is the one place every notification passes.
- **A test host that runs one handler with its behaviours.** The real `AddRelevox` in a test does that already;
  [testing.md](../testing.md) shows how to replace a single handler.
