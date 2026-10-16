using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;

namespace Capar.Internal;

/// <summary>
/// Remembers, per container, which requests have no pipeline behaviours. Used by generated code.
/// </summary>
/// <remarks>
/// <para>
/// Behaviours come from the container, as in MediatR, so they cannot be known at build time: an application
/// can register them before or after <c>AddCapar</c>, closed or open generic. What can be known is that
/// a container's registrations do not change once it is built. The first send of each request type asks
/// the container; if the answer is "none", every later send skips the question and calls the handler
/// directly, which is where the no-behaviour numbers come from.
/// </para>
/// <para>
/// There is one subtlety, found by the differential tests. A built-in behaviour with nothing to do, such as
/// the exception handler behaviour for a request no exception handler names, is left out, but in MediatR its
/// presence still makes the whole send asynchronous: a handler that throws before returning a task produces
/// a faulted task rather than an exception out of <c>Send</c>. Such requests are remembered separately so
/// the generated code can turn a synchronous throw into a faulted task, which costs a try block and nothing
/// on the success path.
/// </para>
/// <para>
/// Registered as a singleton per generated mediator type, so each container gets its own answers. A race
/// between two first sends is harmless: both ask, both store the same answer.
/// </para>
/// </remarks>
/// <typeparam name="TMediator">The generated mediator this state belongs to.</typeparam>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class PipelineState<TMediator>
{
    private const byte Unknown = 0;
    private const byte None = 1;
    private const byte NoneButAsynchronous = 2;
    private const byte Some = 3;

    private readonly byte[] _requests;
    private readonly byte[] _streams;

    /// <summary>Creates the state for a mediator with the given number of request and stream types.</summary>
    /// <param name="requests">Request types, in the generated mediator's order.</param>
    /// <param name="streams">Stream request types, in the generated mediator's order.</param>
    /// <param name="everyRequestHasBehaviours">
    /// True when some behaviour applies to every request type, such as an open generic behaviour or a
    /// built-in processor or exception behaviour. MediatR then runs a pipeline even for a request it has no
    /// handler for, so failing to find one faults the task instead of throwing.
    /// </param>
    public PipelineState(int requests, int streams, bool everyRequestHasBehaviours)
    {
        _requests = new byte[requests];
        _streams = new byte[streams];
        EveryRequestHasBehaviours = everyRequestHasBehaviours;
    }

    /// <summary>Whether some behaviour applies to every request type.</summary>
    public bool EveryRequestHasBehaviours { get; }

    /// <summary>The behaviours for one request type, or null when it has none.</summary>
    /// <typeparam name="TRequest">The request type.</typeparam>
    /// <typeparam name="TResponse">The response type; <see cref="Unit"/> for a request that returns nothing.</typeparam>
    /// <param name="index">The request type's index in the generated mediator.</param>
    /// <param name="services">The container or scope to resolve from.</param>
    /// <param name="asynchronous">
    /// When null is returned: whether behaviours were left out that would have made the send asynchronous.
    /// </param>
    /// <returns>The behaviours, outermost first, or null.</returns>
    public IPipelineBehavior<TRequest, TResponse>[]? Behaviors<TRequest, TResponse>(int index, IServiceProvider services, out bool asynchronous)
        where TRequest : notnull =>
        Resolve<IPipelineBehavior<TRequest, TResponse>>(_requests, index, services, out asynchronous);

    /// <summary>The stream behaviours for one stream request type, or null when it has none.</summary>
    /// <typeparam name="TRequest">The stream request type.</typeparam>
    /// <typeparam name="TResponse">The item type.</typeparam>
    /// <param name="index">The stream request type's index in the generated mediator.</param>
    /// <param name="services">The container or scope to resolve from.</param>
    /// <returns>The behaviours, outermost first, or null.</returns>
    public IStreamPipelineBehavior<TRequest, TResponse>[]? StreamBehaviors<TRequest, TResponse>(int index, IServiceProvider services)
        where TRequest : notnull =>
        Resolve<IStreamPipelineBehavior<TRequest, TResponse>>(_streams, index, services, out _);

    private static T[]? Resolve<T>(byte[] states, int index, IServiceProvider services, out bool asynchronous)
    {
        byte state = states[index];
        if (state is None or NoneButAsynchronous)
        {
            asynchronous = state == NoneButAsynchronous;
            return null;
        }

        IEnumerable<T> resolved = services.GetServices<T>();
        T[] behaviors = resolved as T[] ?? [.. resolved];
        bool droppedAny = false;

        // A built-in behaviour with nothing to run for this request is left out: it would not change what
        // happens, and leaving it out keeps the request on the direct path.
        if (Array.Exists(behaviors, static behavior => behavior is Pipeline.IOptionalBehavior { IsEmpty: true }))
        {
            behaviors = Array.FindAll(behaviors, static behavior => behavior is not Pipeline.IOptionalBehavior { IsEmpty: true });
            droppedAny = true;
        }

        if (behaviors.Length > 0)
        {
            states[index] = Some;
            asynchronous = false;
            return behaviors;
        }

        states[index] = droppedAny ? NoneButAsynchronous : None;
        asynchronous = droppedAny;
        return null;
    }
}
