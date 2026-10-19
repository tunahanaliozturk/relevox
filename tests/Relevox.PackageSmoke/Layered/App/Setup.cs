using Relevox;
using Microsoft.Extensions.DependencyInjection;

namespace App;

public sealed class PingHandler : IRequestHandler<Contracts.Ping, int>
{
    public Task<int> Handle(Contracts.Ping request, CancellationToken cancellationToken) => Task.FromResult(42);
}

public static class Setup
{
    public static IServiceCollection AddApplication(this IServiceCollection services) => services.AddRelevox();
}
