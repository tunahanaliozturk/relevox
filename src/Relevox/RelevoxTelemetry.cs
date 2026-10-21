namespace Relevox;

/// <summary>The name Relevox publishes its telemetry under.</summary>
/// <remarks>
/// Telemetry is off until <c>options.Telemetry.Enabled</c> is set in <c>AddRelevox</c>. The names of spans,
/// attributes and instruments are fixed by ADR 0013 and listed in <c>docs/telemetry.md</c>.
/// </remarks>
public static class RelevoxTelemetry
{
    /// <summary>
    /// The name of the <see cref="System.Diagnostics.ActivitySource"/> Relevox's spans come from and of the
    /// <see cref="System.Diagnostics.Metrics.Meter"/> its measurements come from, for OpenTelemetry's
    /// <c>AddSource</c> and <c>AddMeter</c>.
    /// </summary>
    public const string SourceName = "Relevox";
}
