using Microsoft.Extensions.DependencyInjection;

namespace Scenarios;

public static partial class Library
{
    static partial void Register(IServiceCollection services, ServiceLifetime lifetime) =>
        services.AddMediatR(configuration =>
        {
            configuration.RegisterServicesFromAssembly(typeof(Library).Assembly);
            configuration.Lifetime = lifetime;
        });
}
