using Capar;
using Capar.Pipeline;
using Microsoft.AspNetCore.Diagnostics;

namespace Shop;

/// <summary>A request that can say what is wrong with it.</summary>
public interface IValidated
{
    IEnumerable<(string Field, string Problem)> Problems();
}

public sealed class InvalidRequestException(IReadOnlyDictionary<string, string[]> problems) : Exception("The request is not valid.")
{
    public IReadOnlyDictionary<string, string[]> Problems { get; } = problems;
}

/// <summary>Stops a request with problems before its handler runs, for every request that can have them.</summary>
public sealed class Validation<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (request is IValidated validated)
        {
            Dictionary<string, string[]> problems = validated.Problems()
                .GroupBy(problem => problem.Field)
                .ToDictionary(group => group.Key, group => group.Select(problem => problem.Problem).ToArray());
            if (problems.Count > 0)
            {
                throw new InvalidRequestException(problems);
            }
        }

        return next(cancellationToken);
    }
}

/// <summary>Notes every request before anything else runs.</summary>
public sealed class Received<TRequest>(Activity activity, CurrentCustomer customer) : IRequestPreProcessor<TRequest>
    where TRequest : notnull
{
    public Task Process(TRequest request, CancellationToken cancellationToken)
    {
        activity.Add($"{customer.Name} sent {typeof(TRequest).Name}");
        return Task.CompletedTask;
    }
}

/// <summary>Turns the application's exceptions into problem details, so endpoints stay one line each.</summary>
public sealed class ProblemMapper(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        (int status, Microsoft.AspNetCore.Mvc.ProblemDetails? details) = exception switch
        {
            InvalidRequestException invalid => (StatusCodes.Status400BadRequest, new Microsoft.AspNetCore.Mvc.ValidationProblemDetails(invalid.Problems.ToDictionary())),
            OrderNotFoundException notFound => (StatusCodes.Status404NotFound, new Microsoft.AspNetCore.Mvc.ProblemDetails { Title = notFound.Message }),
            _ => (0, null),
        };

        if (details is null)
        {
            return false;
        }

        httpContext.Response.StatusCode = status;
        details.Status = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = httpContext, ProblemDetails = details, Exception = exception });
    }
}
