using Relevox;

namespace RelevoxApp;

#if (noResponse)
public sealed record RelevoxFeature : IRequest;

public sealed class RelevoxFeatureHandler : IRequestHandler<RelevoxFeature>
{
    public Task Handle(RelevoxFeature request, CancellationToken cancellationToken)
    {
        // Replace with the work this request stands for, and the test with what it should do.
        return Task.CompletedTask;
    }
}
#else
public sealed record RelevoxFeature : IRequest<RelevoxResponse>;

public sealed class RelevoxFeatureHandler : IRequestHandler<RelevoxFeature, RelevoxResponse>
{
    public Task<RelevoxResponse> Handle(RelevoxFeature request, CancellationToken cancellationToken)
    {
        // Replace with the work this request stands for, and the test with what it should answer.
        return Task.FromResult<RelevoxResponse>(default!);
    }
}
#endif
