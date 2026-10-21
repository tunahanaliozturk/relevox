namespace Relevox;

/// <summary>The name Relevox publishes its telemetry under.</summary>
/// <remarks>
/// Telemetry is off until <c>options.Telemetry.Enabled</c> is set in <c>AddRelevox</c>. The names of spans and
/// attributes are fixed by ADR 0013 and listed in <c>docs/telemetry.md</c>.
/// </remarks>
public static class RelevoxTelemetry
{
    /// <summary>
    /// The name of the <see cref="System.Diagnostics.ActivitySource"/> Relevox's spans come from, for
    /// OpenTelemetry's <c>AddSource</c>.
    /// </summary>
    public const string SourceName = "Relevox";
}
