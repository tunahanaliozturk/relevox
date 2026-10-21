// Next to RelevoxOptions, in the namespace AddRelevox is configured from.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Settings for the spans and metrics the generated mediator emits, under <see cref="RelevoxOptions.Telemetry"/>.</summary>
/// <remarks>
/// An object of its own rather than a flag on <see cref="RelevoxOptions"/>, so what comes later, such as a span
/// per notification handler, has somewhere to go (ADR 0013).
/// </remarks>
public sealed class RelevoxTelemetryOptions
{
    /// <summary>
    /// Whether every send, publish and stream enumeration gets a span on the <c>Relevox</c> activity source and
    /// a <c>relevox.operation.duration</c> measurement on the <c>Relevox</c> meter. Off by default.
    /// </summary>
    /// <remarks>
    /// Off, the generated mediator reads this flag once per call and does nothing else. On, it still creates and
    /// records nothing until something listens to <see cref="Relevox.RelevoxTelemetry.SourceName"/>.
    /// </remarks>
    public bool Enabled { get; set; }
}
