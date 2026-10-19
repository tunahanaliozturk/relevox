using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Relevox.Pipeline;

namespace Relevox.Internal;

/// <summary>
/// Knows, for one request type, which exception types have handlers or actions. Generated per request.
/// </summary>
/// <remarks>
/// <para>
/// MediatR finds exception handlers by closing <c>IRequestExceptionHandler&lt;,,&gt;</c> over each type in the
/// thrown exception's hierarchy with <c>MakeGenericType</c>, which needs code generated at run time. The
/// generator knows at build time which exception types any handler for this request names, so the generated
/// subclass walks the hierarchy and resolves only those closed types. Nothing is closed at run time, and the
/// pipeline works under NativeAOT.
/// </para>
/// <para>
/// The walk goes from the thrown type to <see cref="Exception"/>, most specific first, as MediatR's does. A
/// handler reached through more than one exception type runs once, at the most specific one.
/// </para>
/// </remarks>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
[EditorBrowsable(EditorBrowsableState.Never)]
[StackTraceHidden, DebuggerNonUserCode, DebuggerStepThrough]
public abstract class ExceptionCatalog<TRequest, TResponse>
    where TRequest : notnull
{
    /// <summary>Whether any exception handler is known for this request.</summary>
    public abstract bool HasHandlers { get; }

    /// <summary>Whether any exception action is known for this request.</summary>
    public abstract bool HasActions { get; }

    /// <summary>Runs handlers for the exception until one handles it.</summary>
    /// <param name="services">The container or scope.</param>
    /// <param name="request">The request.</param>
    /// <param name="exception">The exception.</param>
    /// <param name="state">The handling state.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes when a handler has handled the exception or none is left.</returns>
    public abstract Task Handle(IServiceProvider services, TRequest request, Exception exception, RequestExceptionHandlerState<TResponse> state, CancellationToken cancellationToken);

    /// <summary>Runs every action for the exception.</summary>
    /// <param name="services">The container or scope.</param>
    /// <param name="request">The request.</param>
    /// <param name="exception">The exception.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes when every action has run.</returns>
    public abstract Task Execute(IServiceProvider services, TRequest request, Exception exception, CancellationToken cancellationToken);

    /// <summary>Runs the handlers registered for one exception type. True once the exception is handled.</summary>
    /// <typeparam name="TException">The exception type at this step of the walk.</typeparam>
    /// <param name="services">The container or scope.</param>
    /// <param name="request">The request.</param>
    /// <param name="exception">The exception, which is a <typeparamref name="TException"/>.</param>
    /// <param name="state">The handling state.</param>
    /// <param name="seen">Handler types already run at a more specific step.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>True when the exception has been handled.</returns>
    protected static async Task<bool> HandleAs<TException>(
        IServiceProvider services,
        TRequest request,
        Exception exception,
        RequestExceptionHandlerState<TResponse> state,
        HashSet<Type> seen,
        CancellationToken cancellationToken)
        where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(seen);

        foreach (IRequestExceptionHandler<TRequest, TResponse, TException> handler in services.GetServices<IRequestExceptionHandler<TRequest, TResponse, TException>>())
        {
            if (!seen.Add(handler.GetType()))
            {
                continue;
            }

            await handler.Handle(request, (TException)exception, state, cancellationToken).ConfigureAwait(false);
            if (state.Handled)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Runs the actions registered for one exception type.</summary>
    /// <typeparam name="TException">The exception type at this step of the walk.</typeparam>
    /// <param name="services">The container or scope.</param>
    /// <param name="request">The request.</param>
    /// <param name="exception">The exception, which is a <typeparamref name="TException"/>.</param>
    /// <param name="seen">Action types already run at a more specific step.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes when the actions have run.</returns>
    protected static async Task ExecuteAs<TException>(
        IServiceProvider services,
        TRequest request,
        Exception exception,
        HashSet<Type> seen,
        CancellationToken cancellationToken)
        where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(seen);

        foreach (IRequestExceptionAction<TRequest, TException> action in services.GetServices<IRequestExceptionAction<TRequest, TException>>())
        {
            if (seen.Add(action.GetType()))
            {
                await action.Execute(request, (TException)exception, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
