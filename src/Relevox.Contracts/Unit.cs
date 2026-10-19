namespace Relevox;

/// <summary>
/// A type with exactly one value, for generic code that needs a response type where there is none.
/// </summary>
/// <remarks>
/// Kept for code moving from MediatR, where <c>IRequest&lt;Unit&gt;</c> was the way to write a request that
/// returns nothing before <see cref="IRequest"/> existed. New code should use <see cref="IRequest"/>.
/// </remarks>
public readonly struct Unit : IEquatable<Unit>, IComparable<Unit>, IComparable
{
    private static readonly Unit Instance;

    /// <summary>The only value.</summary>
    public static ref readonly Unit Value => ref Instance;

    /// <summary>A completed task holding the only value, so returning it never allocates.</summary>
    public static Task<Unit> Task { get; } = System.Threading.Tasks.Task.FromResult(Instance);

    /// <summary>Always zero: every unit is equal.</summary>
    /// <param name="other">Another unit.</param>
    /// <returns>Zero.</returns>
    public int CompareTo(Unit other) => 0;

    /// <summary>Zero for a unit, one for anything else, matching how nulls sort.</summary>
    /// <param name="obj">Anything.</param>
    /// <returns>Zero or one.</returns>
    public int CompareTo(object? obj) => obj is Unit ? 0 : 1;

    /// <summary>Always true.</summary>
    /// <param name="other">Another unit.</param>
    /// <returns>True.</returns>
    public bool Equals(Unit other) => true;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Unit;

    /// <inheritdoc />
    public override int GetHashCode() => 0;

    /// <summary>Renders as an empty tuple.</summary>
    /// <returns><c>()</c>.</returns>
    public override string ToString() => "()";

    /// <summary>Always true.</summary>
    /// <param name="left">A unit.</param>
    /// <param name="right">Another unit.</param>
    /// <returns>True.</returns>
    public static bool operator ==(Unit left, Unit right) => true;

    /// <summary>Always false.</summary>
    /// <param name="left">A unit.</param>
    /// <param name="right">Another unit.</param>
    /// <returns>False.</returns>
    public static bool operator !=(Unit left, Unit right) => false;

    /// <summary>Always false.</summary>
    /// <param name="left">A unit.</param>
    /// <param name="right">Another unit.</param>
    /// <returns>False.</returns>
    public static bool operator <(Unit left, Unit right) => false;

    /// <summary>Always true.</summary>
    /// <param name="left">A unit.</param>
    /// <param name="right">Another unit.</param>
    /// <returns>True.</returns>
    public static bool operator <=(Unit left, Unit right) => true;

    /// <summary>Always false.</summary>
    /// <param name="left">A unit.</param>
    /// <param name="right">Another unit.</param>
    /// <returns>False.</returns>
    public static bool operator >(Unit left, Unit right) => false;

    /// <summary>Always true.</summary>
    /// <param name="left">A unit.</param>
    /// <param name="right">Another unit.</param>
    /// <returns>True.</returns>
    public static bool operator >=(Unit left, Unit right) => true;
}
