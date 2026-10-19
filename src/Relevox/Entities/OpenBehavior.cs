using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Relevox.Entities;

/// <summary>An open generic behaviour and its lifetime, for <see cref="RelevoxOptions.AddOpenBehaviors(IEnumerable{OpenBehavior})"/>.</summary>
/// <param name="openBehaviorType">An open generic type implementing <c>IPipelineBehavior&lt;,&gt;</c>.</param>
/// <param name="serviceLifetime">The behaviour's lifetime.</param>
public sealed class OpenBehavior(
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.Interfaces)] Type openBehaviorType,
    ServiceLifetime serviceLifetime = ServiceLifetime.Transient)
{
    /// <summary>The open generic behaviour type.</summary>
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.Interfaces)]
    public Type OpenBehaviorType { get; } = openBehaviorType;

    /// <summary>The behaviour's lifetime.</summary>
    public ServiceLifetime ServiceLifetime { get; } = serviceLifetime;
}
