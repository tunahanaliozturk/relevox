namespace Scenarios;

// A generic exception handler and action whose exception type parameter is constrained to something narrower than
// Exception, and whose request type parameter is constrained to a marker, so no other scenario's request takes
// them. Relevox 1.0 reported them as RVX0004 and never ran them.
//
// The request throws the constraint's own class. Relevox looks up only the exception types a handler names, and
// for these the class they are constrained to; MediatR would also close them over a subclass thrown here.

public class ShortageException(string message) : Exception(message);

public interface IStockRequest;

/// <summary>Throws a <see cref="ShortageException"/>, or nothing for "plenty".</summary>
public sealed record Restock(string Item) : IRequest<string>, IStockRequest;

public sealed class RestockHandler(Trace trace) : IRequestHandler<Restock, string>
{
    public Task<string> Handle(Restock request, CancellationToken cancellationToken)
    {
        trace.Add($"RestockHandler({request.Item})");
        return request.Item == "plenty" ? Task.FromResult("restocked") : throw new ShortageException(request.Item);
    }
}

public sealed class ShortageNotice<TRequest, TResponse, TException>(Trace trace) : IRequestExceptionHandler<TRequest, TResponse, TException>
    where TRequest : IStockRequest
    where TException : ShortageException
{
    public Task Handle(TRequest request, TException exception, RequestExceptionHandlerState<TResponse> state, CancellationToken cancellationToken)
    {
        trace.Add($"ShortageNotice({exception.Message})");
        return Task.CompletedTask;
    }
}

public sealed class ShortageAlarm<TRequest, TException>(Trace trace) : IRequestExceptionAction<TRequest, TException>
    where TRequest : IStockRequest
    where TException : ShortageException
{
    public Task Execute(TRequest request, TException exception, CancellationToken cancellationToken)
    {
        trace.Add($"ShortageAlarm({exception.Message})");
        return Task.CompletedTask;
    }
}
