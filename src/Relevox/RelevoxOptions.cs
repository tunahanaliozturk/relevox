using System.Diagnostics.CodeAnalysis;
using Relevox;

// In MediatR's namespace for its configuration, so code that configured MediatR without a using for it keeps
// compiling after the migration fix renames the type.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Settings passed to the generated <c>AddRelevox</c>.</summary>
/// <remarks>
/// The behaviour registration methods mirror MediatR 12's configuration, so the lambda passed to
/// <c>AddMediatR</c> can usually move to <c>AddRelevox</c> unchanged apart from the assembly scanning calls,
/// which Relevox does not need.
/// </remarks>
public sealed class RelevoxOptions
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
    /// calling <c>AddRelevox</c>, is resolved once per mediator instance and kept, because the container would
    /// return that same instance on every call anyway. That removes the container lookup from every call after
    /// the first, and is the main reason to choose <see cref="ServiceLifetime.Singleton"/> here for handlers
    /// that hold no per-request state.
    /// </remarks>
    public ServiceLifetime HandlerLifetime { get; set; } = ServiceLifetime.Transient;

    /// <summary>Tracing for sends, publishes and streams. Off by default; see <c>docs/telemetry.md</c>.</summary>
    public RelevoxTelemetryOptions Telemetry { get; } = new();

    /// <summary>
    /// Pipeline behaviours to register, in order. The first one registered runs outermost.
    /// </summary>
    /// <remarks>
    /// Behaviours are resolved from the container on each call, so ones registered directly with the
    /// container, before or after <c>AddRelevox</c>, run too, in container registration order.
    /// </remarks>
    public IList<ServiceDescriptor> BehaviorsToRegister { get; } = [];

    /// <summary>Registers a closed pipeline behaviour for one request type.</summary>
    /// <typeparam name="TServiceType">The behaviour interface, such as <c>IPipelineBehavior&lt;Ping, Pong&gt;</c>.</typeparam>
    /// <typeparam name="TImplementationType">The behaviour class.</typeparam>
    /// <param name="serviceLifetime">The behaviour's lifetime.</param>
    /// <returns>These options, for chaining.</returns>
    public RelevoxOptions AddBehavior<TServiceType, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TImplementationType>(
        ServiceLifetime serviceLifetime = ServiceLifetime.Transient) =>
        AddBehavior(typeof(TServiceType), typeof(TImplementationType), serviceLifetime);

    /// <summary>Registers a behaviour class under every closed <c>IPipelineBehavior&lt;,&gt;</c> it implements.</summary>
    /// <typeparam name="TImplementationType">The behaviour class.</typeparam>
    /// <param name="serviceLifetime">The behaviour's lifetime.</param>
    /// <returns>These options, for chaining.</returns>
    public RelevoxOptions AddBehavior<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.Interfaces)] TImplementationType>(ServiceLifetime serviceLifetime = ServiceLifetime.Transient) =>
        AddBehavior(typeof(TImplementationType), serviceLifetime);

    /// <summary>Registers a behaviour class under every closed <c>IPipelineBehavior&lt;,&gt;</c> it implements.</summary>
    /// <param name="implementationType">The behaviour class.</param>
    /// <param name="serviceLifetime">The behaviour's lifetime.</param>
    /// <returns>These options, for chaining.</returns>
    public RelevoxOptions AddBehavior([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.Interfaces)] Type implementationType, ServiceLifetime serviceLifetime = ServiceLifetime.Transient)
    {
        AddClosed(BehaviorsToRegister, implementationType, typeof(IPipelineBehavior<,>), serviceLifetime);
        return this;
    }

    /// <summary>Registers several open generic behaviours, in order, with one lifetime.</summary>
    /// <remarks>
    /// The types arrive as plain <see cref="Type"/> values, so the trimmer cannot see what to keep. Under
    /// NativeAOT, register closed behaviours instead.
    /// </remarks>
    /// <param name="openBehaviorTypes">Open generic types implementing <c>IPipelineBehavior&lt;,&gt;</c>.</param>
    /// <param name="serviceLifetime">Their lifetime.</param>
    /// <returns>These options, for chaining.</returns>
    [RequiresUnreferencedCode("The behaviour types are not visible to the trimmer. Register closed behaviours under NativeAOT.")]
    public RelevoxOptions AddOpenBehaviors(IEnumerable<Type> openBehaviorTypes, ServiceLifetime serviceLifetime = ServiceLifetime.Transient)
    {
        ArgumentNullException.ThrowIfNull(openBehaviorTypes);
        foreach (Type openBehaviorType in openBehaviorTypes)
        {
            AddOpenBehavior(openBehaviorType, serviceLifetime);
        }

        return this;
    }

    /// <summary>Registers several open generic behaviours, in order, each with its own lifetime.</summary>
    /// <param name="openBehaviors">The behaviours.</param>
    /// <returns>These options, for chaining.</returns>
    public RelevoxOptions AddOpenBehaviors(IEnumerable<Relevox.Entities.OpenBehavior> openBehaviors)
    {
        ArgumentNullException.ThrowIfNull(openBehaviors);
        foreach (Relevox.Entities.OpenBehavior openBehavior in openBehaviors)
        {
            AddOpenBehavior(openBehavior.OpenBehaviorType, openBehavior.ServiceLifetime);
        }

        return this;
    }

    /// <summary>Registers a closed pipeline behaviour for one request type.</summary>
    /// <param name="serviceType">The behaviour interface, such as <c>IPipelineBehavior&lt;Ping, Pong&gt;</c>.</param>
    /// <param name="implementationType">The behaviour class.</param>
    /// <param name="serviceLifetime">The behaviour's lifetime.</param>
    /// <returns>These options, for chaining.</returns>
    public RelevoxOptions AddBehavior(
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
    public RelevoxOptions AddOpenBehavior(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.Interfaces)] Type openBehaviorType,
        ServiceLifetime serviceLifetime = ServiceLifetime.Transient)
    {
        BehaviorsToRegister.Add(new ServiceDescriptor(typeof(IPipelineBehavior<,>), RequireOpen(openBehaviorType, typeof(IPipelineBehavior<,>)), serviceLifetime));
        return this;
    }

    /// <summary>
    /// Whether the pre and post processors found at build time are registered. Off by default.
    /// </summary>
    /// <remarks>
    /// Off because it is off in MediatR 12: its assembly scan skips processors unless this is set, and code
    /// moving from MediatR should not find processors running that never ran before. Processors added with
    /// <see cref="AddRequestPreProcessor{TServiceType, TImplementationType}"/> and its siblings are registered
    /// either way.
    /// </remarks>
    public bool AutoRegisterRequestProcessors { get; set; }

    /// <summary>Stream pipeline behaviours to register, in order. The first one registered runs outermost.</summary>
    public IList<ServiceDescriptor> StreamBehaviorsToRegister { get; } = [];

    /// <summary>Registers a closed stream behaviour for one stream request type.</summary>
    /// <typeparam name="TServiceType">The behaviour interface, such as <c>IStreamPipelineBehavior&lt;Feed, Item&gt;</c>.</typeparam>
    /// <typeparam name="TImplementationType">The behaviour class.</typeparam>
    /// <param name="serviceLifetime">The behaviour's lifetime.</param>
    /// <returns>These options, for chaining.</returns>
    public RelevoxOptions AddStreamBehavior<TServiceType, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TImplementationType>(
        ServiceLifetime serviceLifetime = ServiceLifetime.Transient) =>
        AddStreamBehavior(typeof(TServiceType), typeof(TImplementationType), serviceLifetime);

    /// <summary>Registers a closed stream behaviour for one stream request type.</summary>
    /// <param name="serviceType">The behaviour interface.</param>
    /// <param name="implementationType">The behaviour class.</param>
    /// <param name="serviceLifetime">The behaviour's lifetime.</param>
    /// <returns>These options, for chaining.</returns>
    public RelevoxOptions AddStreamBehavior(
        Type serviceType,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type implementationType,
        ServiceLifetime serviceLifetime = ServiceLifetime.Transient)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        ArgumentNullException.ThrowIfNull(implementationType);
        StreamBehaviorsToRegister.Add(new ServiceDescriptor(serviceType, implementationType, serviceLifetime));
        return this;
    }

    /// <summary>Registers a stream behaviour class under every closed <c>IStreamPipelineBehavior&lt;,&gt;</c> it implements.</summary>
    /// <typeparam name="TImplementationType">The behaviour class.</typeparam>
    /// <param name="serviceLifetime">The behaviour's lifetime.</param>
    /// <returns>These options, for chaining.</returns>
    public RelevoxOptions AddStreamBehavior<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.Interfaces)] TImplementationType>(ServiceLifetime serviceLifetime = ServiceLifetime.Transient) =>
        AddStreamBehavior(typeof(TImplementationType), serviceLifetime);

    /// <summary>Registers a stream behaviour class under every closed <c>IStreamPipelineBehavior&lt;,&gt;</c> it implements.</summary>
    /// <param name="implementationType">The behaviour class.</param>
    /// <param name="serviceLifetime">The behaviour's lifetime.</param>
    /// <returns>These options, for chaining.</returns>
    public RelevoxOptions AddStreamBehavior([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.Interfaces)] Type implementationType, ServiceLifetime serviceLifetime = ServiceLifetime.Transient)
    {
        AddClosed(StreamBehaviorsToRegister, implementationType, typeof(IStreamPipelineBehavior<,>), serviceLifetime);
        return this;
    }

    /// <summary>Registers an open generic stream behaviour for every stream request.</summary>
    /// <param name="openBehaviorType">An open generic type implementing <c>IStreamPipelineBehavior&lt;,&gt;</c>.</param>
    /// <param name="serviceLifetime">The behaviour's lifetime.</param>
    /// <returns>These options, for chaining.</returns>
    public RelevoxOptions AddOpenStreamBehavior(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.Interfaces)] Type openBehaviorType,
        ServiceLifetime serviceLifetime = ServiceLifetime.Transient)
    {
        StreamBehaviorsToRegister.Add(new ServiceDescriptor(typeof(IStreamPipelineBehavior<,>), RequireOpen(openBehaviorType, typeof(IStreamPipelineBehavior<,>)), serviceLifetime));
        return this;
    }

    /// <summary>
    /// The publisher used for notifications, as an instance. A <see cref="Relevox.NotificationPublishers.ForeachAwaitPublisher"/>
    /// by default.
    /// </summary>
    /// <remarks>
    /// With the default, the generated mediator runs handlers itself, one after another, without building an
    /// executor per handler. Any other publisher is handed the handlers the way MediatR hands them over.
    /// </remarks>
    public INotificationPublisher NotificationPublisher { get; set; } = new Relevox.NotificationPublishers.ForeachAwaitPublisher();

    /// <summary>
    /// The publisher used for notifications, as a type the container creates with <see cref="Lifetime"/>. Takes
    /// precedence over <see cref="NotificationPublisher"/> when set.
    /// </summary>
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    public Type? NotificationPublisherType { get; set; }

    /// <summary>Whether exception actions see every exception or only unhandled ones.</summary>
    public RequestExceptionActionProcessorStrategy RequestExceptionActionProcessorStrategy { get; set; }

    /// <summary>Pre-processors to register, in addition to the ones found at build time.</summary>
    public IList<ServiceDescriptor> RequestPreProcessorsToRegister { get; } = [];

    /// <summary>Post-processors to register, in addition to the ones found at build time.</summary>
    public IList<ServiceDescriptor> RequestPostProcessorsToRegister { get; } = [];

    /// <summary>Registers a closed pre-processor.</summary>
    /// <typeparam name="TServiceType">The processor interface, such as <c>IRequestPreProcessor&lt;Ping&gt;</c>.</typeparam>
    /// <typeparam name="TImplementationType">The processor class.</typeparam>
    /// <param name="serviceLifetime">The processor's lifetime.</param>
    /// <returns>These options, for chaining.</returns>
    public RelevoxOptions AddRequestPreProcessor<TServiceType, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TImplementationType>(
        ServiceLifetime serviceLifetime = ServiceLifetime.Transient)
    {
        RequestPreProcessorsToRegister.Add(new ServiceDescriptor(typeof(TServiceType), typeof(TImplementationType), serviceLifetime));
        return this;
    }

    /// <summary>Registers a pre-processor class under every closed <c>IRequestPreProcessor&lt;&gt;</c> it implements.</summary>
    /// <typeparam name="TImplementationType">The processor class.</typeparam>
    /// <param name="serviceLifetime">The processor's lifetime.</param>
    /// <returns>These options, for chaining.</returns>
    public RelevoxOptions AddRequestPreProcessor<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.Interfaces)] TImplementationType>(ServiceLifetime serviceLifetime = ServiceLifetime.Transient) =>
        AddRequestPreProcessor(typeof(TImplementationType), serviceLifetime);

    /// <summary>Registers a pre-processor class under every closed <c>IRequestPreProcessor&lt;&gt;</c> it implements.</summary>
    /// <param name="implementationType">The processor class.</param>
    /// <param name="serviceLifetime">The processor's lifetime.</param>
    /// <returns>These options, for chaining.</returns>
    public RelevoxOptions AddRequestPreProcessor([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.Interfaces)] Type implementationType, ServiceLifetime serviceLifetime = ServiceLifetime.Transient)
    {
        AddClosed(RequestPreProcessorsToRegister, implementationType, typeof(Relevox.Pipeline.IRequestPreProcessor<>), serviceLifetime);
        return this;
    }

    /// <summary>Registers a closed pre-processor.</summary>
    /// <param name="serviceType">The processor interface.</param>
    /// <param name="implementationType">The processor class.</param>
    /// <param name="serviceLifetime">The processor's lifetime.</param>
    /// <returns>These options, for chaining.</returns>
    public RelevoxOptions AddRequestPreProcessor(
        Type serviceType,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type implementationType,
        ServiceLifetime serviceLifetime = ServiceLifetime.Transient)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        ArgumentNullException.ThrowIfNull(implementationType);
        RequestPreProcessorsToRegister.Add(new ServiceDescriptor(serviceType, implementationType, serviceLifetime));
        return this;
    }

    /// <summary>Registers an open generic pre-processor that applies to every request.</summary>
    /// <param name="openBehaviorType">An open generic type implementing <c>IRequestPreProcessor&lt;&gt;</c>.</param>
    /// <param name="serviceLifetime">The processor's lifetime.</param>
    /// <returns>These options, for chaining.</returns>
    public RelevoxOptions AddOpenRequestPreProcessor(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.Interfaces)] Type openBehaviorType,
        ServiceLifetime serviceLifetime = ServiceLifetime.Transient)
    {
        RequestPreProcessorsToRegister.Add(new ServiceDescriptor(typeof(Relevox.Pipeline.IRequestPreProcessor<>), RequireOpen(openBehaviorType, typeof(Relevox.Pipeline.IRequestPreProcessor<>)), serviceLifetime));
        return this;
    }

    /// <summary>Registers a closed post-processor.</summary>
    /// <typeparam name="TServiceType">The processor interface, such as <c>IRequestPostProcessor&lt;Ping, Pong&gt;</c>.</typeparam>
    /// <typeparam name="TImplementationType">The processor class.</typeparam>
    /// <param name="serviceLifetime">The processor's lifetime.</param>
    /// <returns>These options, for chaining.</returns>
    public RelevoxOptions AddRequestPostProcessor<TServiceType, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TImplementationType>(
        ServiceLifetime serviceLifetime = ServiceLifetime.Transient)
    {
        RequestPostProcessorsToRegister.Add(new ServiceDescriptor(typeof(TServiceType), typeof(TImplementationType), serviceLifetime));
        return this;
    }

    /// <summary>Registers a post-processor class under every closed <c>IRequestPostProcessor&lt;,&gt;</c> it implements.</summary>
    /// <typeparam name="TImplementationType">The processor class.</typeparam>
    /// <param name="serviceLifetime">The processor's lifetime.</param>
    /// <returns>These options, for chaining.</returns>
    public RelevoxOptions AddRequestPostProcessor<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.Interfaces)] TImplementationType>(ServiceLifetime serviceLifetime = ServiceLifetime.Transient) =>
        AddRequestPostProcessor(typeof(TImplementationType), serviceLifetime);

    /// <summary>Registers a post-processor class under every closed <c>IRequestPostProcessor&lt;,&gt;</c> it implements.</summary>
    /// <param name="implementationType">The processor class.</param>
    /// <param name="serviceLifetime">The processor's lifetime.</param>
    /// <returns>These options, for chaining.</returns>
    public RelevoxOptions AddRequestPostProcessor([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.Interfaces)] Type implementationType, ServiceLifetime serviceLifetime = ServiceLifetime.Transient)
    {
        AddClosed(RequestPostProcessorsToRegister, implementationType, typeof(Relevox.Pipeline.IRequestPostProcessor<,>), serviceLifetime);
        return this;
    }

    /// <summary>Registers a closed post-processor.</summary>
    /// <param name="serviceType">The processor interface.</param>
    /// <param name="implementationType">The processor class.</param>
    /// <param name="serviceLifetime">The processor's lifetime.</param>
    /// <returns>These options, for chaining.</returns>
    public RelevoxOptions AddRequestPostProcessor(
        Type serviceType,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type implementationType,
        ServiceLifetime serviceLifetime = ServiceLifetime.Transient)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        ArgumentNullException.ThrowIfNull(implementationType);
        RequestPostProcessorsToRegister.Add(new ServiceDescriptor(serviceType, implementationType, serviceLifetime));
        return this;
    }

    /// <summary>Registers an open generic post-processor that applies to every request.</summary>
    /// <param name="openBehaviorType">An open generic type implementing <c>IRequestPostProcessor&lt;,&gt;</c>.</param>
    /// <param name="serviceLifetime">The processor's lifetime.</param>
    /// <returns>These options, for chaining.</returns>
    public RelevoxOptions AddOpenRequestPostProcessor(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.Interfaces)] Type openBehaviorType,
        ServiceLifetime serviceLifetime = ServiceLifetime.Transient)
    {
        RequestPostProcessorsToRegister.Add(new ServiceDescriptor(typeof(Relevox.Pipeline.IRequestPostProcessor<,>), RequireOpen(openBehaviorType, typeof(Relevox.Pipeline.IRequestPostProcessor<,>)), serviceLifetime));
        return this;
    }

    private static void AddClosed(
        IList<ServiceDescriptor> list,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.Interfaces)] Type implementationType,
        Type openContract,
        ServiceLifetime serviceLifetime)
    {
        ArgumentNullException.ThrowIfNull(implementationType);

        Type[] contracts =
        [
            .. implementationType
                .GetInterfaces()
                .Where(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == openContract),
        ];
        if (contracts.Length == 0)
        {
            throw new InvalidOperationException($"{implementationType.Name} must implement {openContract.FullName}.");
        }

        foreach (Type contract in contracts)
        {
            list.Add(new ServiceDescriptor(contract, implementationType, serviceLifetime));
        }
    }

    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.Interfaces)]
    private static Type RequireOpen(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.Interfaces)] Type type,
        Type contract)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (!type.IsGenericTypeDefinition)
        {
            throw new InvalidOperationException($"{type.Name} must be an open generic type, such as LoggingBehavior<,>.");
        }

        bool implements = type
            .GetInterfaces()
            .Any(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == contract);
        return implements
            ? type
            : throw new InvalidOperationException($"{type.Name} must implement {contract.FullName}.");
    }
}
