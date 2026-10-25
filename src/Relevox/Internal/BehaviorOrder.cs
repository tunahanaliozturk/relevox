using System.Reflection;

namespace Relevox.Internal;

/// <summary>
/// Where <see cref="PipelineOrderAttribute"/> moves a request type's behaviours, learned on its first send: for each
/// place in the running order, the place in the container's order it is taken from (ADR 0019).
/// </summary>
/// <remarks>
/// <para>
/// The container's own order cannot be changed: it is registration order, and a container may hand out the same
/// array on every call, for behaviours that are all singletons. So a request type whose behaviours the attribute
/// moves gets a new array on each send, one small allocation. One whose behaviours are already in order, the case
/// for every application that does not use the attribute, gets no order at all and runs as before.
/// </para>
/// <para>
/// The types the order was learned from are kept and compared on each send, so a scope that returns other
/// behaviours, as a child scope in Autofac can, is never run in another list's order.
/// </para>
/// </remarks>
internal sealed class BehaviorOrder
{
    private readonly int[] _sources;
    private readonly Type[] _types;

    private BehaviorOrder(int[] sources, Type[] types)
    {
        _sources = sources;
        _types = types;
    }

    /// <summary>The place in the container's order that the given running place is taken from.</summary>
    /// <param name="position">The place in the running order.</param>
    public int this[int position] => _sources[position];

    /// <summary>The order the attribute gives the behaviours, or null when it is the container's.</summary>
    /// <typeparam name="T">The behaviour interface.</typeparam>
    /// <param name="behaviors">The behaviours, in the order the container returned them.</param>
    /// <returns>The order, or null.</returns>
    public static BehaviorOrder? For<T>(T[] behaviors)
        where T : class
    {
        int[] keys = new int[behaviors.Length];
        bool sorted = true;
        for (int position = 0; position < behaviors.Length; position++)
        {
            keys[position] = behaviors[position].GetType().GetCustomAttribute<PipelineOrderAttribute>(inherit: true)?.Order ?? 0;
            sorted &= position == 0 || keys[position - 1] <= keys[position];
        }

        if (sorted)
        {
            return null;
        }

        // OrderBy is stable, so behaviours with the same number keep the container's order.
        int[] sources = [.. Enumerable.Range(0, keys.Length).OrderBy(position => keys[position])];
        return new BehaviorOrder(sources, [.. behaviors.Select(static behavior => behavior.GetType())]);
    }

    /// <summary>Whether the container returned the same behaviour types, in the same order, as the order was learned from.</summary>
    /// <typeparam name="T">The behaviour interface.</typeparam>
    /// <param name="behaviors">What the container returned.</param>
    /// <returns>True when the order applies.</returns>
    public bool Fits<T>(T[] behaviors)
        where T : class
    {
        if (behaviors.Length != _types.Length)
        {
            return false;
        }

        for (int position = 0; position < behaviors.Length; position++)
        {
            if (behaviors[position].GetType() != _types[position])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The behaviours in running order, in a new array.</summary>
    /// <typeparam name="T">The behaviour interface.</typeparam>
    /// <param name="behaviors">What the container returned.</param>
    /// <returns>The behaviours, outermost first.</returns>
    public T[] Apply<T>(T[] behaviors)
    {
        T[] running = new T[_sources.Length];
        for (int position = 0; position < running.Length; position++)
        {
            running[position] = behaviors[_sources[position]];
        }

        return running;
    }
}
