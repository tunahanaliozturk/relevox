using Relevox;
using Relevox.Pipeline;

namespace Contracts;

public sealed record Ping : IRequest<int>;

// An open processor in a library with no mediator of its own. The application's mediator registers it; the
// host below the application must not take it as a reason to generate a second one. As in MediatR, it only
// runs once a processor is added through the options, so this one is only here to be found.
public sealed class Log<TRequest> : IRequestPreProcessor<TRequest>
    where TRequest : notnull
{
    public Task Process(TRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
}
