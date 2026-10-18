using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace Capar.Internal;

/// <summary>
/// What a container's registrations mean for dispatch, learned on first use and remembered. Used by
/// generated code.
/// </summary>
/// <remarks>
/// <para>
/// Behaviours come from the container, as in MediatR, so they cannot be known at build time: an application
/// can register them before or after <c>AddCapar</c>, closed or open generic. What can be known is that a
/// container's registrations do not change once it is built. The first send of each request type asks the
/// container; if the answer is "none", every later send skips the question with one byte read and calls the
/// handler directly.
/// </para>
/// <para>
/// Built-in behaviours are registered as <see cref="BuiltInSlot{TRequest, TResponse}"/>s, which keep their
/// place in the container's order and cost nothing to resolve. The first send of a request type works out
/// which slots have something to do. The others are skipped by position, with a bit mask the pipeline reads,
/// so a request whose built-ins are all idle pays for neither the behaviours nor a filtered copy of the array.
/// In MediatR a skipped built-in would still be there, outermost and <c>async</c>, so a handler that throws
/// before returning a task would produce a faulted task rather than an exception out of <c>Send</c>.
/// Requests that skip one are remembered as asynchronous, and the generated code turns a synchronous throw
/// into a faulted task for them.
/// </para>
/// <para>
/// Registered as a singleton per generated mediator type, so each container gets its own answers. A race
/// between two first uses is harmless: both ask, both store the same answer.
/// </para>
/// </remarks>
/// <typeparam name="TMediator">The generated mediator this state belongs to.</typeparam>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class PipelineState<TMediator>
{
    private const byte Unknown = 0;
    private const byte Direct = 1;
    private const byte DirectButAsynchronous = 2;
    private const byte Pipeline = 3;
    private const byte PipelineWithSlots = 4;

    private readonly byte[] _requests;
    private readonly SlotPlan?[] _plans;
    private readonly byte[] _streams;
    private readonly bool _builtInsForEveryRequest;
    private byte _everyRequest;
    private byte _sequentialPublish;

    /// <summary>Creates the state for a mediator with the given number of request and stream types.</summary>
    /// <param name="requests">Request types, in the generated mediator's order.</param>
    /// <param name="streams">Stream request types, in the generated mediator's order.</param>
    /// <param name="builtInsForEveryRequest">
    /// True when <c>AddCapar</c> registered a built-in processor or exception behaviour, which MediatR applies
    /// to every request type.
    /// </param>
    public PipelineState(int requests, int streams, bool builtInsForEveryRequest)
    {
        _requests = new byte[requests];
        _plans = new SlotPlan?[requests];
        _streams = new byte[streams];
        _builtInsForEveryRequest = builtInsForEveryRequest;
    }

    /// <summary>
    /// The direct path: true when the request is known to have no behaviours, with whether a synchronous throw
    /// must become a faulted task. A plain byte read, so it inlines into the generated send.
    /// </summary>
    /// <param name="index">The request type's index in the generated mediator.</param>
    /// <param name="asynchronous">Whether to fault the task instead of throwing.</param>
    /// <returns>True when the handler can be called directly.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDirect(int index, out bool asynchronous)
    {
        byte state = _requests[index];
        asynchronous = state == DirectButAsynchronous;
        return state is Direct or DirectButAsynchronous;
    }

    /// <summary>The behaviours for one request type, or null when it has none.</summary>
    /// <typeparam name="TRequest">The request type.</typeparam>
    /// <typeparam name="TResponse">The response type; <see cref="Unit"/> for a request that returns nothing.</typeparam>
    /// <param name="index">The request type's index in the generated mediator.</param>
    /// <param name="services">The container or scope to resolve from.</param>
    /// <param name="asynchronous">Whether a synchronous throw from the send must become a faulted task.</param>
    /// <param name="skip">Positions in the returned array the pipeline must pass over, as a bit mask.</param>
    /// <returns>The behaviours, outermost first, or null.</returns>
    public IPipelineBehavior<TRequest, TResponse>[]? Behaviors<TRequest, TResponse>(int index, IServiceProvider services, out bool asynchronous, out ulong skip)
        where TRequest : notnull
    {
        skip = 0;
        byte state = _requests[index];
        if (state is Direct or DirectButAsynchronous)
        {
            asynchronous = state == DirectButAsynchronous;
            return null;
        }

        IEnumerable<IPipelineBehavior<TRequest, TResponse>> resolved = services.GetServices<IPipelineBehavior<TRequest, TResponse>>();
        IPipelineBehavior<TRequest, TResponse>[] behaviors = resolved as IPipelineBehavior<TRequest, TResponse>[] ?? [.. resolved];

        if (state == Pipeline)
        {
            asynchronous = false;
            return behaviors;
        }

        if (state == Unknown)
        {
            return Learn(index, behaviors, services, out asynchronous, out skip);
        }

        SlotPlan plan = Volatile.Read(ref _plans[index])!;
        asynchronous = plan.Asynchronous;
        return plan.Apply(behaviors, services, out skip);
    }

    /// <summary>The stream behaviours for one stream request type, or null when it has none.</summary>
    /// <typeparam name="TRequest">The stream request type.</typeparam>
    /// <typeparam name="TResponse">The item type.</typeparam>
    /// <param name="index">The stream request type's index in the generated mediator.</param>
    /// <param name="services">The container or scope to resolve from.</param>
    /// <returns>The behaviours, outermost first, or null.</returns>
    public IStreamPipelineBehavior<TRequest, TResponse>[]? StreamBehaviors<TRequest, TResponse>(int index, IServiceProvider services)
        where TRequest : notnull =>
        Resolve<IStreamPipelineBehavior<TRequest, TResponse>>(_streams, index, services, out _);

    /// <summary>
    /// Whether some behaviour applies to every request type. MediatR then runs a pipeline even for a request it
    /// has no handler for, so failing to find one faults the task instead of throwing.
    /// </summary>
    /// <param name="services">The container or scope.</param>
    /// <returns>True when every request has at least one behaviour.</returns>
    public bool EveryRequestHasBehaviours(IServiceProvider services)
    {
        if (_everyRequest == Unknown)
        {
            // An open generic behaviour registered at any point is seen by asking for the behaviours of a request
            // type nothing else knows about. A constrained one that would not close over it would not apply to an
            // arbitrary request either.
            bool open = services.GetServices<IPipelineBehavior<Probe, Unit>>().Any();
            _everyRequest = _builtInsForEveryRequest || open ? (byte)2 : (byte)1;
        }

        return _everyRequest == 2;
    }

    /// <summary>
    /// Whether the notification publisher in force is the default sequential one, which the generated mediator
    /// runs inline. Read from the container on first use, so a publisher registered after <c>AddCapar</c> wins,
    /// as it does in MediatR.
    /// </summary>
    /// <param name="services">The container or scope.</param>
    /// <returns>True for <see cref="NotificationPublishers.ForeachAwaitPublisher"/>.</returns>
    public bool SequentialPublish(IServiceProvider services)
    {
        if (_sequentialPublish == Unknown)
        {
            _sequentialPublish = services.GetService<INotificationPublisher>() is NotificationPublishers.ForeachAwaitPublisher ? (byte)2 : (byte)1;
        }

        return _sequentialPublish == 2;
    }

    /// <summary>
    /// The first send of a request type: finds the slots, asks each whether its behaviour has anything to do,
    /// and remembers the answer. The behaviours created to ask are the ones this send runs.
    /// </summary>
    private IPipelineBehavior<TRequest, TResponse>[]? Learn<TRequest, TResponse>(
        int index,
        IPipelineBehavior<TRequest, TResponse>[] behaviors,
        IServiceProvider services,
        out bool asynchronous,
        out ulong skip)
        where TRequest : notnull
    {
        List<int> active = [];
        List<int> idle = [];
        IPipelineBehavior<TRequest, TResponse>[]? running = null;
        for (int position = 0; position < behaviors.Length; position++)
        {
            if (behaviors[position] is not BuiltInSlot<TRequest, TResponse> slot)
            {
                continue;
            }

            IPipelineBehavior<TRequest, TResponse> created = slot.Create(services);
            if (created is Capar.Pipeline.IOptionalBehavior { IsEmpty: true })
            {
                idle.Add(position);
                continue;
            }

            active.Add(position);
            running ??= (IPipelineBehavior<TRequest, TResponse>[])behaviors.Clone();
            running[position] = created;
        }

        if (active.Count == 0 && idle.Count == 0)
        {
            _requests[index] = Pipeline;
            asynchronous = false;
            skip = 0;
            return behaviors;
        }

        SlotPlan plan = new([.. active], [.. idle], behaviors.Length);
        asynchronous = plan.Asynchronous;
        if (plan.Kept == 0)
        {
            _requests[index] = plan.Asynchronous ? DirectButAsynchronous : Direct;
            skip = 0;
            return null;
        }

        Volatile.Write(ref _plans[index], plan);
        _requests[index] = PipelineWithSlots;
        return plan.Finish(running ?? behaviors, out skip);
    }

    private static T[]? Resolve<T>(byte[] states, int index, IServiceProvider services, out bool asynchronous)
    {
        byte state = states[index];
        if (state is Direct or DirectButAsynchronous)
        {
            asynchronous = state == DirectButAsynchronous;
            return null;
        }

        IEnumerable<T> resolved = services.GetServices<T>();
        T[] behaviors = resolved as T[] ?? [.. resolved];

        if (state is Pipeline)
        {
            asynchronous = false;
            return behaviors;
        }

        // A behaviour with nothing to run for this request is left out: it would not change what happens, apart
        // from making the send asynchronous, which the caller reproduces. Requests use slots instead; this path
        // is for streams, where an application may still register such a behaviour itself.
        bool dropped = state == PipelineWithSlots
            || (state == Unknown && Array.Exists(behaviors, static behavior => behavior is Capar.Pipeline.IOptionalBehavior { IsEmpty: true }));
        if (dropped)
        {
            behaviors = Array.FindAll(behaviors, static behavior => behavior is not Capar.Pipeline.IOptionalBehavior { IsEmpty: true });
        }

        asynchronous = dropped;
        if (state == Unknown)
        {
            states[index] = behaviors.Length == 0
                ? dropped ? DirectButAsynchronous : Direct
                : dropped ? PipelineWithSlots : Pipeline;
        }

        return behaviors.Length == 0 ? null : behaviors;
    }

    /// <summary>A request type that exists only to ask the container about open generic behaviours.</summary>
    private sealed class Probe : IRequest<Unit>;

    /// <summary>What one request type's slots do, learned on its first send.</summary>
    /// <param name="active">Positions of slots whose behaviour has work, replaced on every send.</param>
    /// <param name="idle">Positions of slots with nothing to do, skipped.</param>
    /// <param name="length">How many behaviours the container returns for the request.</param>
    private sealed class SlotPlan(int[] active, int[] idle, int length)
    {
        // The pipeline reads skipped positions from a 64-bit mask. Past that, the idle slots are copied out.
        private readonly bool _masked = length <= 64;
        private readonly ulong _skip = Mask(idle, length);

        public bool Asynchronous => idle.Length > 0;

        public int Kept => length - idle.Length;

        public IPipelineBehavior<TRequest, TResponse>[] Apply<TRequest, TResponse>(IPipelineBehavior<TRequest, TResponse>[] behaviors, IServiceProvider services, out ulong skip)
            where TRequest : notnull
        {
            if (active.Length == 0 && _masked)
            {
                // Nothing to replace, so the container's array is used as it is: no copy, no allocation.
                skip = _skip;
                return behaviors;
            }

            // The container may hand out the same array again, so it is copied before anything is replaced.
            IPipelineBehavior<TRequest, TResponse>[] running = (IPipelineBehavior<TRequest, TResponse>[])behaviors.Clone();
            foreach (int position in active)
            {
                running[position] = ((BuiltInSlot<TRequest, TResponse>)running[position]).Create(services);
            }

            return Finish(running, out skip);
        }

        public T[] Finish<T>(T[] running, out ulong skip)
        {
            if (_masked)
            {
                skip = _skip;
                return running;
            }

            skip = 0;
            T[] kept = new T[Kept];
            for (int position = 0, next = 0; position < running.Length; position++)
            {
                if (Array.IndexOf(idle, position) < 0)
                {
                    kept[next++] = running[position];
                }
            }

            return kept;
        }

        private static ulong Mask(int[] positions, int length)
        {
            ulong mask = 0;
            if (length <= 64)
            {
                foreach (int position in positions)
                {
                    mask |= 1UL << position;
                }
            }

            return mask;
        }
    }
}
