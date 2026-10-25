using FluentValidation;
using FluentValidation.Results;

namespace Relevox.FluentValidation;

/// <summary>
/// Runs every FluentValidation validator registered for the request before the rest of the pipeline, and throws
/// if any of them fails. Registered with <c>AddValidation</c>, which gives it only to the requests the build found
/// a validator for.
/// </summary>
/// <remarks>
/// <para>
/// Validators run one after another, in the order the container returns them, so validators that share a scoped
/// dependency such as a <c>DbContext</c> never use it at the same time. The failures of all of them are collected
/// and thrown together, in the order the validators ran, as the behaviour in Clean Architecture's template does.
/// </para>
/// <para>
/// The exception is FluentValidation's <see cref="ValidationException"/>. An application with an exception of its
/// own derives from this class, overrides <see cref="CreateException"/> and passes the derived class to
/// <c>AddValidation</c>.
/// </para>
/// </remarks>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
/// <param name="validators">The validators registered for the request.</param>
public class ValidationBehavior<TRequest, TResponse>(IEnumerable<IValidator<TRequest>> validators) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    /// <inheritdoc />
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);

        List<ValidationFailure>? failures = null;
        foreach (IValidator<TRequest> validator in validators)
        {
            ValidationResult result = await validator.ValidateAsync(new ValidationContext<TRequest>(request), cancellationToken).ConfigureAwait(false);
            if (!result.IsValid)
            {
                (failures ??= []).AddRange(result.Errors);
            }
        }

        if (failures is not null)
        {
            throw CreateException(failures);
        }

        return await next(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The exception thrown when validation fails.</summary>
    /// <param name="failures">Every failure of every validator, in the order they ran.</param>
    /// <returns>FluentValidation's <see cref="ValidationException"/> with the failures, unless overridden.</returns>
    protected virtual Exception CreateException(IReadOnlyList<ValidationFailure> failures) => new ValidationException(failures);
}
