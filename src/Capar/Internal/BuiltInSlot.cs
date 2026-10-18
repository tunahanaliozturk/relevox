using System.ComponentModel;
using Capar.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace Capar.Internal;

/// <summary>
/// Holds a built-in behaviour's place among the behaviours the container returns. Registered by generated
/// code; not meant to be used directly.
/// </summary>
/// <remarks>
/// <para>
/// MediatR registers its exception and processor behaviours with the container, so where they run depends on
/// what else was registered before and after. Çapar keeps that order by registering a slot in the same place,
/// but a slot is a singleton with no dependencies: resolving it costs nothing per send. The generated mediator
/// works out once per request type whether the behaviour behind each slot has anything to do, leaves out the
/// slots that do not, and creates the real behaviour only for the ones that do.
/// </para>
/// <para>
/// Before slots, the real behaviours were registered and resolved on every send, each looking up its
/// processors or its exception catalog, only to be thrown away for the many requests they did not apply to.
/// </para>
/// </remarks>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
[EditorBrowsable(EditorBrowsableState.Never)]
public abstract class BuiltInSlot<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private protected BuiltInSlot()
    {
    }

    /// <summary>Creates the behaviour this slot stands for, over the container or scope of the send.</summary>
    /// <param name="services">The container or scope.</param>
    /// <returns>The behaviour.</returns>
    internal abstract IPipelineBehavior<TRequest, TResponse> Create(IServiceProvider services);

    /// <summary>Not called by Çapar, which replaces or skips every slot before running the pipeline.</summary>
    /// <param name="request">The request.</param>
    /// <param name="next">The rest of the pipeline.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Never returns.</returns>
    /// <exception cref="NotSupportedException">Always.</exception>
    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "This is a placeholder that keeps a built-in Capar behaviour's place among the pipeline behaviours. "
            + "Send requests through ISender or IMediator, which replace it with the real behaviour.");
}

/// <summary>The place of <see cref="RequestPreProcessorBehavior{TRequest, TResponse}"/>.</summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class PreProcessorSlot<TRequest, TResponse> : BuiltInSlot<TRequest, TResponse>
    where TRequest : notnull
{
    internal override IPipelineBehavior<TRequest, TResponse> Create(IServiceProvider services) =>
        new RequestPreProcessorBehavior<TRequest, TResponse>(services.GetServices<IRequestPreProcessor<TRequest>>());
}

/// <summary>The place of <see cref="RequestPostProcessorBehavior{TRequest, TResponse}"/>.</summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class PostProcessorSlot<TRequest, TResponse> : BuiltInSlot<TRequest, TResponse>
    where TRequest : notnull
{
    internal override IPipelineBehavior<TRequest, TResponse> Create(IServiceProvider services) =>
        new RequestPostProcessorBehavior<TRequest, TResponse>(services.GetServices<IRequestPostProcessor<TRequest, TResponse>>());
}

/// <summary>The place of <see cref="RequestExceptionProcessorBehavior{TRequest, TResponse}"/>.</summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class ExceptionHandlerSlot<TRequest, TResponse> : BuiltInSlot<TRequest, TResponse>
    where TRequest : notnull
{
    internal override IPipelineBehavior<TRequest, TResponse> Create(IServiceProvider services) =>
        new RequestExceptionProcessorBehavior<TRequest, TResponse>(services);
}

/// <summary>The place of <see cref="RequestExceptionActionProcessorBehavior{TRequest, TResponse}"/>.</summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class ExceptionActionSlot<TRequest, TResponse> : BuiltInSlot<TRequest, TResponse>
    where TRequest : notnull
{
    internal override IPipelineBehavior<TRequest, TResponse> Create(IServiceProvider services) =>
        new RequestExceptionActionProcessorBehavior<TRequest, TResponse>(services);
}
