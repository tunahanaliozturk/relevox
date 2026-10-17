using Capar;

/// <summary>A request nothing handles. The packaged analyzer has to report it as CPR0001.</summary>
public sealed record Orphan : IRequest<int>;
