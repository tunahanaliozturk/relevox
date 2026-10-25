using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace Relevox.Internal;

/// <summary>
/// What a container's registrations mean for dispatch, learned on first use and remembered. Used by
/// generated code.
/// </summary>
/// <remarks>
/// <para>
/// Behaviours come from the container, as in MediatR, so they cannot be known at build time: an application
/// can register them before or after <c>AddRelevox</c>, closed or open generic. What can be known is that a
/// container's registrations do not change once it is built. The first send of each request type asks the
/// container; if the answer is "none", every later send skips the question with one byte read and calls the
/// handler directly.
/// </para>
/// <para>
/// Built-in behaviours are registered as <see cref="BuiltInSlot{TRequest, TResponse}"/>s, which keep their
/// place in the container's order and cost nothing to resolve. The first send of a request type works out
/// which slots have something to do; from then on the idle ones are left out of the array the pipeline gets,
/// or kept as an <c>async</c> pass-through where one of the application's behaviours sits outside them, and the
/// others are replaced by their behaviours, with nothing created for the idle ones.
/// In MediatR a skipped built-in would still be there, outermost and <c>async</c>, so a handler that throws
/// before returning a task would produce a faulted task rather than an exception out of <c>Send</c>.
/// Requests that skip one are remembered as asynchronous, and the generated code turns a synchronous throw
/// into a faulted task for them.
/// </para>
/// <para>
/// Behaviours that carry <see cref="PipelineOrderAttribute"/> are put in its order on the same first send. A
/// request type whose behaviours are already in that order, as they are wherever the attribute is not used, is
/// remembered exactly as it would be without it.
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
    private const byte PipelineWithPlan = 4;

    private readonly byte[] _requests;
    private readonly SlotPlan?[] _plans;
    private readonly byte[] _streams;
    private readonly BehaviorOrder?[] _streamOrders;
    private readonly bool _builtInsForEveryRequest;
    private byte _everyRequest;
    private byte _sequentialPublish;

    /// <summary>Creates the state for a mediator with the given number of request and stream types.</summary>
    /// <param name="requests">Request types, in the generated mediator's order.</param>
    /// <param name="streams">Stream request types, in the generated mediator's order.</param>
    /// <param name="builtInsForEveryRequest">
    /// True when <c>AddRelevox</c> registered a built-in processor or exception behaviour, which MediatR applies
    /// to every request type.
    /// </param>
    public PipelineState(int requests, int streams, bool builtInsForEveryRequest)
    {
        _requests = new byte[requests];
        _plans = new SlotPlan?[requests];
        _streams = new byte[streams];
        _streamOrders = new BehaviorOrder?[streams];
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
    /// <returns>The behaviours, outermost first, or null.</returns>
    public IPipelineBehavior<TRequest, TResponse>[]? Behaviors<TRequest, TResponse>(int index, IServiceProvider services, out bool asynchronous)
        where TRequest : notnull
    {
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
            return Learn(index, behaviors, services, out asynchronous, remember: true);
        }

        // The plan is read on its own, not through the state byte, so a thread that sees the state before the
        // plan simply works the roles out again. So does a container whose scopes add behaviours of their own,
        // which return an array of another shape than the one the plan was learned from.
        if (Volatile.Read(ref _plans[index]) is { } plan && plan.Fits(behaviors))
        {
            asynchronous = plan.Asynchronous;
            return plan.Apply(behaviors, services);
        }

        return Learn(index, behaviors, services, out asynchronous, remember: false);
    }

    /// <summary>The stream behaviours for one stream request type, or null when it has none.</summary>
    /// <typeparam name="TRequest">The stream request type.</typeparam>
    /// <typeparam name="TResponse">The item type.</typeparam>
    /// <param name="index">The stream request type's index in the generated mediator.</param>
    /// <param name="services">The container or scope to resolve from.</param>
    /// <returns>The behaviours, outermost first, or null.</returns>
    public IStreamPipelineBehavior<TRequest, TResponse>[]? StreamBehaviors<TRequest, TResponse>(int index, IServiceProvider services)
        where TRequest : notnull
    {
        byte state = _streams[index];
        if (state == Direct)
        {
            return null;
        }

        IEnumerable<IStreamPipelineBehavior<TRequest, TResponse>> resolved = services.GetServices<IStreamPipelineBehavior<TRequest, TResponse>>();
        IStreamPipelineBehavior<TRequest, TResponse>[] behaviors = resolved as IStreamPipelineBehavior<TRequest, TResponse>[] ?? [.. resolved];
        if (state == Pipeline)
        {
            return behaviors;
        }

        if (state == PipelineWithPlan && Volatile.Read(ref _streamOrders[index]) is { } known && known.Fits(behaviors))
        {
            return known.Apply(behaviors);
        }

        BehaviorOrder? order = behaviors.Length == 0 ? null : BehaviorOrder.For(behaviors);
        if (state == Unknown)
        {
            if (order is null)
            {
                _streams[index] = behaviors.Length == 0 ? Direct : Pipeline;
            }
            else
            {
                Volatile.Write(ref _streamOrders[index], order);
                Volatile.Write(ref _streams[index], PipelineWithPlan);
            }
        }

        return behaviors.Length == 0 ? null : order?.Apply(behaviors) ?? behaviors;
    }

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
    /// runs inline. Read from the container on first use, so a publisher registered after <c>AddRelevox</c> wins,
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
    /// The first send of a request type: puts the behaviours in the order <see cref="PipelineOrderAttribute"/>
    /// gives, finds the slots, asks each whether its behaviour has anything to do, and remembers the answer. The
    /// behaviours created to ask are the ones this send runs.
    /// </summary>
    private IPipelineBehavior<TRequest, TResponse>[]? Learn<TRequest, TResponse>(
        int index,
        IPipelineBehavior<TRequest, TResponse>[] behaviors,
        IServiceProvider services,
        out bool asynchronous,
        bool remember)
        where TRequest : notnull
    {
        // Slots are judged in running order, because whether an idle one can go depends on what runs outside it.
        BehaviorOrder? order = behaviors.Length == 0 ? null : BehaviorOrder.For(behaviors);
        IPipelineBehavior<TRequest, TResponse>[] running = order?.Apply(behaviors) ?? behaviors;
        IPipelineBehavior<TRequest, TResponse>?[] created = new IPipelineBehavior<TRequest, TResponse>?[running.Length];
        bool anySlot = false;
        for (int position = 0; position < running.Length; position++)
        {
            if (running[position] is BuiltInSlot<TRequest, TResponse> slot)
            {
                anySlot = true;
                created[position] = slot.Create(services);
            }
        }

        // No behaviours at all is the common case, and it must end on the direct path: remembered as a pipeline,
        // every later send asked the container for an empty list and ran an empty chain, about 10 ns each.
        if (!anySlot && order is null)
        {
            if (remember)
            {
                _requests[index] = behaviors.Length == 0 ? Direct : Pipeline;
            }

            asynchronous = false;
            return behaviors.Length == 0 ? null : behaviors;
        }

        SlotPlan plan = SlotPlan.For(running, created, order);
        asynchronous = plan.Asynchronous;
        if (plan.Kept == 0)
        {
            if (remember)
            {
                _requests[index] = plan.Asynchronous ? DirectButAsynchronous : Direct;
            }

            return null;
        }

        if (remember)
        {
            Volatile.Write(ref _plans[index], plan);
            Volatile.Write(ref _requests[index], PipelineWithPlan);
        }

        return plan.Apply(behaviors, services, created);
    }

    /// <summary>A request type that exists only to ask the container about open generic behaviours.</summary>
    private sealed class Probe : IRequest<Unit>;

    /// <summary>What one request type's slots do, and the order its behaviours run in, learned on its first send.</summary>
    /// <remarks>
    /// <para>
    /// Idle slots are copied out rather than skipped in place: the copy is one small array, and keeping the
    /// pipeline itself unaware of slots keeps the common send, with no built-ins at all, as cheap as before.
    /// A skip mask in the chain cost that send 8 bytes and a fifth of its time.
    /// </para>
    /// <para>
    /// A MediatR built-in is <c>async</c>, so even with nothing to do it turns a synchronous throw from
    /// everything inside it into a faulted task. Where it sits outside every behaviour of the application's,
    /// the generated code reproduces that for the whole send. Where an application's behaviour sits outside it,
    /// as one registered before <c>AddRelevox</c> does, that behaviour must still see a faulted task, so the slot
    /// becomes a shared pass-through that awaits instead of disappearing.
    /// </para>
    /// </remarks>
    private sealed class SlotPlan
    {
        private const byte Keep = 0;
        private const byte Idle = 1;
        private const byte Active = 2;
        private const byte PassThrough = 3;

        private readonly byte[] _roles;
        private readonly int[] _slots;
        private readonly BehaviorOrder? _order;

        private SlotPlan(byte[] roles, bool asynchronous, BehaviorOrder? order)
        {
            _roles = roles;
            _slots = [.. Enumerable.Range(0, roles.Length).Where(position => roles[position] != Keep)];
            _order = order;
            Asynchronous = asynchronous;
            Kept = Array.FindAll(roles, static role => role != Idle).Length;
        }

        public bool Asynchronous { get; }

        public int Kept { get; }

        /// <summary>Works out each position's role from the behaviours in running order and what the slots created.</summary>
        public static SlotPlan For<TRequest, TResponse>(IPipelineBehavior<TRequest, TResponse>[] behaviors, IPipelineBehavior<TRequest, TResponse>?[] created, BehaviorOrder? order)
            where TRequest : notnull
        {
            byte[] roles = new byte[behaviors.Length];
            bool asynchronous = false;

            // What runs immediately outside the current position: nothing yet, an async built-in, or one of the
            // application's own behaviours, which may not be async.
            byte outside = Idle;
            for (int position = 0; position < behaviors.Length; position++)
            {
                if (created[position] is null)
                {
                    roles[position] = Keep;
                    outside = Keep;
                }
                else if (created[position] is not Relevox.Pipeline.IOptionalBehavior { IsEmpty: true })
                {
                    roles[position] = Active;
                    outside = Active;
                }
                else if (outside == Keep)
                {
                    roles[position] = PassThrough;
                    outside = PassThrough;
                }
                else
                {
                    roles[position] = Idle;
                    asynchronous |= outside == Idle;
                }
            }

            return new SlotPlan(roles, asynchronous, order);
        }

        /// <summary>Whether the container returned an array of the shape the plan was learned from.</summary>
        public bool Fits<TRequest, TResponse>(IPipelineBehavior<TRequest, TResponse>[] behaviors)
            where TRequest : notnull
        {
            if (_order is not null)
            {
                return _order.Fits(behaviors);
            }

            if (behaviors.Length != _roles.Length)
            {
                return false;
            }

            // Only the slot positions are checked: slots come from AddRelevox alone, so a list of the same length with
            // a slot everywhere one was learned has not moved.
            foreach (int position in _slots)
            {
                if (behaviors[position] is not BuiltInSlot<TRequest, TResponse>)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// The behaviours to run: idle slots left out, active ones replaced. A new array, because the container
        /// may hand out the same one again.
        /// </summary>
        /// <param name="behaviors">What the container returned, slots included, in the container's order.</param>
        /// <param name="services">The container or scope of the send, for the behaviours that are created.</param>
        /// <param name="created">On the first send, the behaviours already created to ask, in running order; otherwise null.</param>
        /// <returns>The behaviours to run.</returns>
        public IPipelineBehavior<TRequest, TResponse>[] Apply<TRequest, TResponse>(
            IPipelineBehavior<TRequest, TResponse>[] behaviors,
            IServiceProvider services,
            IPipelineBehavior<TRequest, TResponse>?[]? created = null)
            where TRequest : notnull
        {
            IPipelineBehavior<TRequest, TResponse>[] running = new IPipelineBehavior<TRequest, TResponse>[Kept];
            for (int position = 0, next = 0; position < behaviors.Length; position++)
            {
                int source = _order is null ? position : _order[position];
                switch (_roles[position])
                {
                    case Keep:
                        running[next++] = behaviors[source];
                        break;
                    case Active:
                        running[next++] = created?[position] ?? ((BuiltInSlot<TRequest, TResponse>)behaviors[source]).Create(services);
                        break;
                    case PassThrough:
                        running[next++] = AsyncPassThrough<TRequest, TResponse>.Instance;
                        break;
                    default:
                        break;
                }
            }

            return running;
        }
    }
}
