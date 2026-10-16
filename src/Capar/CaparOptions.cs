using Microsoft.Extensions.DependencyInjection;

namespace Capar;

/// <summary>Settings passed to the generated <c>AddCapar</c>.</summary>
public sealed class CaparOptions
{
    /// <summary>
    /// The lifetime of every handler and of the mediator itself. Transient by default, as in MediatR.
    /// </summary>
    /// <remarks>
    /// With <see cref="ServiceLifetime.Scoped"/> or <see cref="ServiceLifetime.Singleton"/>, each mediator
    /// instance resolves a handler once and keeps it, because the container would hand back the same
    /// instance anyway. That removes the container lookup from every call after the first. With
    /// <see cref="ServiceLifetime.Transient"/> the handler is resolved on every call, so a handler that keeps
    /// state between calls behaves as it did under MediatR.
    /// </remarks>
    public ServiceLifetime Lifetime { get; set; } = ServiceLifetime.Transient;
}
