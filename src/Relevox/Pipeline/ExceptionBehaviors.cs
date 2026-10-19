using Microsoft.Extensions.DependencyInjection;
using Relevox.Internal;

namespace Relevox.Pipeline;

/// <summary>
/// Catches an exception from the rest of the pipeline and offers it to the request's
/// <see cref="IRequestExceptionHandler{TRequest, TResponse, TException}"/>s, most specific exception type
/// first, until one handles it. Rethrows when none does.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
public sealed class RequestExceptionProcessorBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>, IOptionalBehavior
    where TRequest : notnull
{
    private readonly IServiceProvider _services;
    private readonly ExceptionCatalog<TRequest, TResponse>? _catalog;

    /// <summary>Creates the behaviour over the container its handlers come from.</summary>
    /// <param name="serviceProvider">The container or scope.</param>
    public RequestExceptionProcessorBehavior(IServiceProvider serviceProvider)
    {
        _services = serviceProvider;
        _catalog = serviceProvider.GetService<ExceptionCatalog<TRequest, TResponse>>();
    }

    bool IOptionalBehavior.IsEmpty => _catalog is not { HasHandlers: true };

    /// <inheritdoc />
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);

        try
        {
            return await next(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (_catalog is { HasHandlers: true })
        {
            RequestExceptionHandlerState<TResponse> state = new();
            await _catalog.Handle(_services, request, exception, state, cancellationToken).ConfigureAwait(false);

            // Handled with no response is not handled, as in MediatR: SetHandled(null) rethrows.
            if (!state.Handled || state.Response is null)
            {
                throw;
            }

            return state.Response;
        }
    }
}

/// <summary>
/// Catches an exception from the rest of the pipeline, runs every
/// <see cref="IRequestExceptionAction{TRequest, TException}"/> for it, most specific exception type first, and
/// rethrows.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
public sealed class RequestExceptionActionProcessorBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>, IOptionalBehavior
    where TRequest : notnull
{
    private readonly IServiceProvider _services;
    private readonly ExceptionCatalog<TRequest, TResponse>? _catalog;

    /// <summary>Creates the behaviour over the container its actions come from.</summary>
    /// <param name="serviceProvider">The container or scope.</param>
    public RequestExceptionActionProcessorBehavior(IServiceProvider serviceProvider)
    {
        _services = serviceProvider;
        _catalog = serviceProvider.GetService<ExceptionCatalog<TRequest, TResponse>>();
    }

    bool IOptionalBehavior.IsEmpty => _catalog is not { HasActions: true };

    /// <inheritdoc />
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);

        try
        {
            return await next(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (_catalog is { HasActions: true })
        {
            await _catalog.Execute(_services, request, exception, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }
}
