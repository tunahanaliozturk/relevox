using Microsoft.Extensions.DependencyInjection;

namespace Scenarios;

public static partial class Library
{
    static partial void Bridge(IServiceCollection services, ServiceLifetime lifetime)
    {
        services.AddRelevox(options => options.Lifetime = lifetime);
        services.AddMediatRBridge();
    }
}
