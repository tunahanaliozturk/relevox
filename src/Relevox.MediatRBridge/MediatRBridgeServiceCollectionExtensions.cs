using Microsoft.Extensions.DependencyInjection.Extensions;
using Relevox.MediatRBridge;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Puts the bridge behind MediatR's interfaces.</summary>
public static class MediatRBridgeServiceCollectionExtensions
{
    // The key MediatR's own mediator is kept under, so the bridge can call it once it has taken its place.
    private static readonly object InnerKey = new();

    /// <summary>
    /// Registers MediatR's <c>IMediator</c>, <c>ISender</c> and <c>IPublisher</c> as a bridge: a message whose
    /// type implements Relevox's interface goes to Relevox, anything else to MediatR.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MediatR's own registration stays: its handlers, behaviours and processors are still found by
    /// <c>AddMediatR</c>, and the mediator it registered is kept and called for the messages that have not
    /// moved. Relevox's side comes from the generated <c>AddRelevox</c>. The calls can come in any order, but a
    /// custom <c>MediatorImplementationType</c> is only kept when <c>AddMediatR</c> comes first.
    /// </para>
    /// <para>
    /// A notification implementing both interfaces runs MediatR's handlers, then Relevox's. Once no handler is
    /// left on MediatR, remove this call, <c>AddMediatR</c> and the MediatR package.
    /// </para>
    /// </remarks>
    /// <param name="services">The application's services.</param>
    /// <returns>The services, for chaining.</returns>
    public static IServiceCollection AddMediatRBridge(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.Any(static descriptor => descriptor.IsKeyedService && ReferenceEquals(descriptor.ServiceKey, InnerKey)))
        {
            return services;
        }

        ServiceDescriptor? registered = services.LastOrDefault(static descriptor =>
            descriptor.ServiceType == typeof(MediatR.IMediator) && !descriptor.IsKeyedService);
        services.Add(Inner(registered));

        // MediatR adds its interfaces with TryAdd, so an AddMediatR after this one leaves the bridge in place.
        _ = services.RemoveAll<MediatR.IMediator>().RemoveAll<MediatR.ISender>().RemoveAll<MediatR.IPublisher>();
        ServiceLifetime lifetime = registered?.Lifetime ?? ServiceLifetime.Transient;
        services.Add(new ServiceDescriptor(
            typeof(MediatR.IMediator),
            static provider => new BridgedMediator(
                provider.GetRequiredKeyedService<MediatR.IMediator>(InnerKey),
                provider.GetRequiredService<Relevox.IMediator>()),
            lifetime));
        services.Add(new ServiceDescriptor(typeof(MediatR.ISender), static provider => provider.GetRequiredService<MediatR.IMediator>(), lifetime));
        services.Add(new ServiceDescriptor(typeof(MediatR.IPublisher), static provider => provider.GetRequiredService<MediatR.IMediator>(), lifetime));
        return services;
    }

    private static ServiceDescriptor Inner(ServiceDescriptor? registered) => registered switch
    {
        null => new ServiceDescriptor(typeof(MediatR.IMediator), InnerKey, typeof(MediatR.Mediator), ServiceLifetime.Transient),
        { ImplementationInstance: { } instance } => new ServiceDescriptor(typeof(MediatR.IMediator), InnerKey, instance),
        { ImplementationFactory: { } factory } => new ServiceDescriptor(
            typeof(MediatR.IMediator),
            InnerKey,
            (provider, _) => factory(provider),
            registered.Lifetime),
        _ => new ServiceDescriptor(typeof(MediatR.IMediator), InnerKey, registered.ImplementationType!, registered.Lifetime),
    };
}
