using Relevox;
using Relevox.NotificationPublishers;
using Relevox.Testing;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the Relevox test helpers that work with a real container.</summary>
public static class RelevoxTestingServiceCollectionExtensions
{
    /// <summary>
    /// Records every notification the mediator publishes in a <see cref="NotificationRecorder"/>, which the test
    /// resolves from the container. Call it after <c>AddRelevox</c>, so the publisher it wraps is the one the
    /// application chose.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddRelevoxNotificationRecorder(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // A second call would wrap the first call's recording publisher and record everything twice.
        if (services.Any(static descriptor => descriptor.ServiceType == typeof(NotificationRecorder)))
        {
            return services;
        }

        // The container resolves the last unkeyed registration, so that is the one to wrap.
        ServiceDescriptor? inForce = services.LastOrDefault(static descriptor => descriptor.ServiceType == typeof(INotificationPublisher) && !descriptor.IsKeyedService);
        if (inForce is not null)
        {
            services.Remove(inForce);
        }

        services.AddSingleton(static _ => new NotificationRecorder());
        services.Add(new ServiceDescriptor(
            typeof(INotificationPublisher),
            provider => new RecordingPublisher(provider.GetRequiredService<NotificationRecorder>(), Inner(provider, inForce), ownsInner: inForce is { ImplementationFactory: not null } or { ImplementationType: not null }),
            inForce?.Lifetime ?? ServiceLifetime.Singleton));
        return services;
    }

    private static INotificationPublisher Inner(IServiceProvider provider, ServiceDescriptor? inForce) =>
        inForce switch
        {
            null => new ForeachAwaitPublisher(),
            { ImplementationInstance: INotificationPublisher instance } => instance,
            { ImplementationFactory: { } factory } => (INotificationPublisher)factory(provider),
            { ImplementationType: { } type } => (INotificationPublisher)ActivatorUtilities.CreateInstance(provider, type),
            _ => new ForeachAwaitPublisher(),
        };
}
