using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Relevox.DifferentialTests;

/// <summary>
/// Checks that the configuration lambda written for <c>AddMediatR</c> still compiles against
/// <see cref="RelevoxOptions"/>, member by member.
/// </summary>
/// <remarks>
/// The M2 review found MediatR overloads Relevox did not have, which would have broken migrations at compile
/// time. This test reads both types and fails on any MediatR member without a Relevox counterpart of the same
/// name and parameter count, apart from the ones listed with the reason they do not apply.
/// </remarks>
public sealed class ApiParityTests
{
    /// <summary>MediatR members with no Relevox equivalent, and why.</summary>
    private static readonly Dictionary<string, string> NotApplicable = new(StringComparer.Ordinal)
    {
        ["RegisterServicesFromAssembly"] = "Relevox finds handlers at build time; there is nothing to scan.",
        ["RegisterServicesFromAssemblies"] = "As above.",
        ["RegisterServicesFromAssemblyContaining"] = "As above.",
        ["AssembliesToRegister"] = "As above.",
        ["TypeEvaluator"] = "Filtered the assembly scan.",
        ["RegisterGenericHandlers"] = "Relevox closes generic handlers at build time, whatever this says (ADR 0008).",
        ["MaxGenericTypeParameters"] = "Limits for closing generic handlers at run time; the generator has its own.",
        ["MaxGenericTypeRegistrations"] = "As above.",
        ["MaxTypesClosing"] = "As above.",
        ["RegistrationTimeout"] = "As above.",
        ["MediatorImplementationType"] = "The mediator is generated per assembly.",
    };

    [Fact]
    public void Every_MediatR_configuration_member_has_a_Relevox_counterpart()
    {
        HashSet<string> relevox = [.. Members(typeof(RelevoxOptions))];

        string[] missing =
        [
            .. Members(typeof(MediatRServiceConfiguration))
                .Where(member => !NotApplicable.ContainsKey(member[..member.IndexOf('/', StringComparison.Ordinal)]))
                .Where(member => !relevox.Contains(member))
                .Order(StringComparer.Ordinal),
        ];

        missing.ShouldBeEmpty();
        Members(typeof(MediatRServiceConfiguration)).Count().ShouldBeGreaterThan(30, "the comparison read MediatR's members");
    }

    /// <summary>Public instance members as "Name/parameter count", properties as "Name/property".</summary>
    private static IEnumerable<string> Members(Type type)
    {
        foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (!method.IsSpecialName)
            {
                yield return $"{method.Name}/{method.GetParameters().Length}";
            }
        }

        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            yield return $"{property.Name}/property";
        }
    }
}
