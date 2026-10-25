using System.Diagnostics.CodeAnalysis;
using Relevox.FluentValidation;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Adds the FluentValidation behaviour to <c>AddRelevox</c>'s options.</summary>
public static class RelevoxValidationOptionsExtensions
{
    /// <summary>
    /// Adds <see cref="ValidationBehavior{TRequest, TResponse}"/> at this point of the pipeline, for the requests
    /// the build found a validator for.
    /// </summary>
    /// <remarks>
    /// The generated <c>AddRelevox</c> registers the behaviour closed, for each request with an
    /// <c>IValidator&lt;TRequest&gt;</c> in the project or the projects it references; a request without one gets
    /// no behaviour at all (ADR 0020). Validators themselves are registered as usual, for example with
    /// FluentValidation's <c>AddValidatorsFromAssembly</c>.
    /// </remarks>
    /// <param name="options">The options passed to <c>AddRelevox</c>.</param>
    /// <param name="serviceLifetime">The behaviour's lifetime.</param>
    /// <returns>The options, for chaining.</returns>
    public static RelevoxOptions AddValidation(this RelevoxOptions options, ServiceLifetime serviceLifetime = ServiceLifetime.Transient) =>
        options.AddValidation(typeof(ValidationBehavior<,>), serviceLifetime);

    /// <summary>
    /// Adds a class derived from <see cref="ValidationBehavior{TRequest, TResponse}"/>, such as one that throws an
    /// exception of the application's own, at this point of the pipeline, for the requests the build found a
    /// validator for.
    /// </summary>
    /// <param name="options">The options passed to <c>AddRelevox</c>.</param>
    /// <param name="behaviorType">An open generic class with the same two type parameters, such as <c>Validation&lt;,&gt;</c>.</param>
    /// <param name="serviceLifetime">The behaviour's lifetime.</param>
    /// <returns>The options, for chaining.</returns>
    /// <exception cref="ArgumentException">The type is not an open generic class derived from the behaviour.</exception>
    public static RelevoxOptions AddValidation(
        this RelevoxOptions options,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.Interfaces)] Type behaviorType,
        ServiceLifetime serviceLifetime = ServiceLifetime.Transient)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(behaviorType);

        if (!behaviorType.IsGenericTypeDefinition || behaviorType.GetGenericArguments().Length != 2 || !DerivesFromValidation(behaviorType))
        {
            throw new ArgumentException($"{behaviorType.Name} must be an open generic class with two type parameters, derived from ValidationBehavior<TRequest, TResponse>.", nameof(behaviorType));
        }

        return options.AddOpenBehavior(behaviorType, serviceLifetime);
    }

    private static bool DerivesFromValidation(Type type)
    {
        for (Type? current = type; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(ValidationBehavior<,>))
            {
                return true;
            }
        }

        return false;
    }
}
