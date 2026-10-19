namespace Relevox.Pipeline;

/// <summary>
/// Gets a chance to handle an exception thrown while a request was handled, and to supply a response instead.
/// </summary>
/// <typeparam name="TRequest">The request type, or a base type or interface of it.</typeparam>
/// <typeparam name="TResponse">The response type; <see cref="Unit"/> for a request that returns nothing.</typeparam>
/// <typeparam name="TException">The exception type, or a base type of it.</typeparam>
public interface IRequestExceptionHandler<in TRequest, TResponse, in TException>
    where TRequest : notnull
    where TException : Exception
{
    /// <summary>Looks at the exception and, if it can, marks it handled with a response.</summary>
    /// <param name="request">The request.</param>
    /// <param name="exception">The exception.</param>
    /// <param name="state">Call <see cref="RequestExceptionHandlerState{TResponse}.SetHandled"/> on it to handle the exception.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes when this handler is done.</returns>
    Task Handle(TRequest request, TException exception, RequestExceptionHandlerState<TResponse> state, CancellationToken cancellationToken);
}

/// <summary>
/// Runs when a request's handling throws, for side effects such as logging. Cannot handle the exception.
/// </summary>
/// <typeparam name="TRequest">The request type, or a base type or interface of it.</typeparam>
/// <typeparam name="TException">The exception type, or a base type of it.</typeparam>
public interface IRequestExceptionAction<in TRequest, in TException>
    where TRequest : notnull
    where TException : Exception
{
    /// <summary>Does whatever this action does with the exception.</summary>
    /// <param name="request">The request.</param>
    /// <param name="exception">The exception.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes when this action is done.</returns>
    Task Execute(TRequest request, TException exception, CancellationToken cancellationToken);
}

/// <summary>Carries whether an exception has been handled, and the response that replaces it.</summary>
/// <typeparam name="TResponse">The response type.</typeparam>
public sealed class RequestExceptionHandlerState<TResponse>
{
    /// <summary>Whether a handler has handled the exception.</summary>
    public bool Handled { get; private set; }

    /// <summary>The response to return instead of throwing, once <see cref="Handled"/> is true.</summary>
    public TResponse? Response { get; private set; }

    /// <summary>Marks the exception handled; the request returns <paramref name="response"/> instead of throwing.</summary>
    /// <param name="response">The response to return.</param>
    public void SetHandled(TResponse response)
    {
        Handled = true;
        Response = response;
    }
}
