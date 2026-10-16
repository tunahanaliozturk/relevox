using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Capar;

/// <summary>Settings passed to the generated <c>AddCapar</c>.</summary>
/// <remarks>
/// The behaviour registration methods mirror MediatR 12's configuration, so the lambda passed to
/// <c>AddMediatR</c> can usually move to <c>AddCapar</c> unchanged apart from the assembly scanning calls,
/// which Çapar does not need.
/// </remarks>
public sealed class CaparOptions
{
    /// <summary>
    /// The lifetime of the mediator itself, registered as <see cref="IMediator"/>, <see cref="ISender"/> and
    /// <see cref="IPublisher"/>. Transient by default.
    /// </summary>
    /// <remarks>
    /// This is what MediatR 12's <c>Lifetime</c> setting does too: it applies to the mediator, not to the
    /// handlers, which stay transient whatever it is set to. Use <see cref="HandlerLifetime"/> for those.
    /// </remarks>
    public ServiceLifetime Lifetime { get; set; } = ServiceLifetime.Transient;

    /// <summary>
    /// The lifetime handlers are registered with, when the application has not registered them itself.
    /// Transient by default, as in MediatR.
    /// </summary>
    /// <remarks>
    /// A handler registered as scoped or singleton, whether through this setting or by the application before
    /// calling <c>AddCapar</c>, is resolved once per mediator instance and kept, because the container would
    /// return that same instance on every call anyway. That removes the container lookup from every call after
    /// the first, and is the main reason to choose <see cref="ServiceLifetime.Singleton"/> here for handlers
    /// that hold no per-request state.
    /// </remarks>
    public ServiceLifetime HandlerLifetime { get; set; } = ServiceLifetime.Transient;

    /// <summary>
    /// Pipeline behaviours to register, in order. The first one registered runs outermost.
    /// </summary>
    /// <remarks>
    /// Behaviours are resolved from the container on each call, so ones registered directly with the
    /// container, before or after <c>AddCapar</c>, run too, in container registration order.
    /// </remarks>
    public IList<ServiceDescriptor> BehaviorsToRegister { get; } = [];

    /// <summary>Registers a closed pipeline behaviour for one request type.</summary>
    /// <typeparam name="TServiceType">The behaviour interface, such as <c>IPipelineBehavior&lt;Ping, Pong&gt;</c>.</typeparam>
    /// <typeparam name="TImplementationType">The behaviour class.</typeparam>
    /// <param name="serviceLifetime">The behaviour's lifetime.</param>
    /// <returns>These options, for chaining.</returns>
    public CaparOptions AddBehavior<TServiceType, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TImplementationType>(
        ServiceLifetime serviceLifetime = ServiceLifetime.Transient) =>
        AddBehavior(typeof(TServiceType), typeof(TImplementationType), serviceLifetime);

    /// <summary>Registers a closed pipeline behaviour for one request type.</summary>
    /// <param name="serviceType">The behaviour interface, such as <c>IPipelineBehavior&lt;Ping, Pong&gt;</c>.</param>
    /// <param name="implementationType">The behaviour class.</param>
    /// <param name="serviceLifetime">The behaviour's lifetime.</param>
    /// <returns>These options, for chaining.</returns>
    public CaparOptions AddBehavior(
        Type serviceType,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type implementationType,
        ServiceLifetime serviceLifetime = ServiceLifetime.Transient)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        ArgumentNullException.ThrowIfNull(implementationType);
        BehaviorsToRegister.Add(new ServiceDescriptor(serviceType, implementationType, serviceLifetime));
        return this;
    }

    /// <summary>
    /// Registers an open generic pipeline behaviour, such as <c>LoggingBehavior&lt;,&gt;</c>, for every
    /// request whose types satisfy its constraints.
    /// </summary>
    /// <remarks>
    /// The container closes the behaviour over each request type when it is first resolved, which needs code
    /// generated at run time. Under NativeAOT, prefer <see cref="AddBehavior(Type, Type, ServiceLifetime)"/>
    /// with closed types.
    /// </remarks>
    /// <param name="openBehaviorType">An open generic type implementing <c>IPipelineBehavior&lt;,&gt;</c>.</param>
    /// <param name="serviceLifetime">The behaviour's lifetime.</param>
    /// <returns>These options, for chaining.</returns>
    public CaparOptions AddOpenBehavior(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.Interfaces)] Type openBehaviorType,
        ServiceLifetime serviceLifetime = ServiceLifetime.Transient)
    {
        ArgumentNullException.ThrowIfNull(openBehaviorType);

        if (!openBehaviorType.IsGenericTypeDefinition)
        {
            throw new InvalidOperationException($"{openBehaviorType.Name} must be an open generic type, such as LoggingBehavior<,>.");
        }

        bool implements = openBehaviorType
            .GetInterfaces()
            .Any(contract => contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IPipelineBehavior<,>));
        if (!implements)
        {
            throw new InvalidOperationException($"{openBehaviorType.Name} must implement {typeof(IPipelineBehavior<,>).FullName}.");
        }

        BehaviorsToRegister.Add(new ServiceDescriptor(typeof(IPipelineBehavior<,>), openBehaviorType, serviceLifetime));
        return this;
    }
}
