using Microsoft.Extensions.DependencyInjection;

namespace Capar;

/// <summary>Settings passed to the generated <c>AddCapar</c>.</summary>
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
}
