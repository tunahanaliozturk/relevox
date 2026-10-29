# Relevox.MediatRBridge

[GitHub](https://github.com/tunahanaliozturk/relevox) ·
[Migration guide](https://github.com/tunahanaliozturk/relevox/blob/main/docs/migration.md#one-feature-at-a-time) ·
[Changelog](https://github.com/tunahanaliozturk/relevox/blob/main/CHANGELOG.md)

Runs MediatR and [Relevox](https://github.com/tunahanaliozturk/relevox), the source-generated mediator with
MediatR 12's handler signatures, side by side, so a solution can move to Relevox one feature at a time. The
bridge takes MediatR's `IMediator`, `ISender` and `IPublisher`: a message whose type implements Relevox's
interface goes to Relevox, anything else to MediatR.

```bash
dotnet add package Relevox
dotnet add package Relevox.MediatRBridge
```

```csharp
services.AddMediatR(options => options.RegisterServicesFromAssemblyContaining<Program>());
services.AddRelevox();
services.AddMediatRBridge();
```

A moved request keeps MediatR's interface next to Relevox's, so its callers compile unchanged:
`record PlaceOrder : MediatR.IRequest<int>, Relevox.IRequest<int>`. Each request runs the pipeline of the
library that handles it. A notification with handlers on both sides runs MediatR's, then Relevox's.

Works with MediatR 12.0 and later. MediatR 12.x is Apache-2.0; 13 and later need the application's own MediatR
licence. Once nothing is left on MediatR, remove the bridge and MediatR.
