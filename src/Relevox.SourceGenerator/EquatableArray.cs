using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Relevox.SourceGenerator;

/// <summary>
/// An immutable array that compares by content.
/// </summary>
/// <remarks>
/// Roslyn caches an incremental pipeline stage by comparing its output with the previous run's. Arrays and
/// <c>ImmutableArray&lt;T&gt;</c> compare by reference, so a model holding one never compares equal and the
/// cache never hits. The generator still emits the right code, and silently re-runs everything on every
/// keystroke. Every collection that crosses a stage boundary goes through this type instead.
/// </remarks>
/// <typeparam name="T">An element type that itself compares by value.</typeparam>
internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IEnumerable<T>
    where T : IEquatable<T>
{
    private readonly T[]? _items;

    public EquatableArray(IEnumerable<T> items)
    {
        _items = [.. items];
    }

    public static EquatableArray<T> Empty { get; } = new([]);

    public int Length => _items?.Length ?? 0;

    public T this[int index] => _items![index];

    public bool Equals(EquatableArray<T> other) =>
        (_items ?? []).SequenceEqual(other._items ?? []);

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        int hash = 17;
        foreach (T item in _items ?? [])
        {
            hash = unchecked((hash * 31) + item.GetHashCode());
        }

        return hash;
    }

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)(_items ?? [])).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Builds an <see cref="EquatableArray{T}"/> from a sequence.</summary>
internal static class EquatableArray
{
    public static EquatableArray<T> ToEquatableArray<T>(this IEnumerable<T> items)
        where T : IEquatable<T> =>
        new(items);
}
