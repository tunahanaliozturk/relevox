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

    /// <summary>MediatR 12.5 types with no Relevox counterpart, and why.</summary>
    private static readonly Dictionary<string, string> TypesNotApplicable = new(StringComparer.Ordinal)
    {
        ["MediatR.Mediator"] = "The mediator is generated per assembly, as RelevoxMediator; code takes IMediator, ISender or IPublisher.",
        ["MediatR.Registration.ServiceRegistrar"] = "Drives the assembly scan; Relevox registers at build time.",
        ["MediatR.Wrappers.NotificationHandlerWrapper"] = "Run-time dispatch machinery, replaced by generated code.",
        ["MediatR.Wrappers.NotificationHandlerWrapperImpl`1"] = "As above.",
        ["MediatR.Wrappers.RequestHandlerBase"] = "As above.",
        ["MediatR.Wrappers.RequestHandlerWrapper"] = "As above.",
        ["MediatR.Wrappers.RequestHandlerWrapperImpl`1"] = "As above.",
        ["MediatR.Wrappers.RequestHandlerWrapperImpl`2"] = "As above.",
        ["MediatR.Wrappers.RequestHandlerWrapper`1"] = "As above.",
        ["Microsoft.Extensions.DependencyInjection.ServiceCollectionExtensions"] = "Holds AddMediatR; AddRelevox is generated into each application.",
    };

    [Fact]
    public void Every_MediatR_type_has_a_Relevox_counterpart()
    {
        // Every public type of MediatR 12.5, by full name with MediatR's namespace read as Relevox's, has to exist,
        // so that code written against it compiles after the migration fix. This pins the 1.0 surface.
        HashSet<string> relevox =
        [
            .. new[] { typeof(global::Relevox.IMediator).Assembly, typeof(RelevoxOptions).Assembly }
                .SelectMany(static assembly => assembly.GetExportedTypes())
                .Select(static type => type.FullName!),
        ];

        Type[] mediatr = [.. new[] { typeof(MediatR.IMediator).Assembly, typeof(MediatR.INotification).Assembly }.Distinct().SelectMany(static assembly => assembly.GetExportedTypes())];
        string[] missing =
        [
            .. mediatr
                .Select(static type => type.FullName!)
                .Where(name => !TypesNotApplicable.ContainsKey(name))
                .Where(name => !relevox.Contains(Counterpart(name)))
                .Order(StringComparer.Ordinal),
        ];

        missing.ShouldBeEmpty();
        mediatr.Length.ShouldBeGreaterThan(30, "the comparison read MediatR's types");
    }

    private static string Counterpart(string name) => name switch
    {
        "Microsoft.Extensions.DependencyInjection.MediatRServiceConfiguration" => "Microsoft.Extensions.DependencyInjection.RelevoxOptions",
        "Microsoft.Extensions.DependencyInjection.RequestExceptionActionProcessorStrategy" => "Relevox.RequestExceptionActionProcessorStrategy",
        _ when name.StartsWith("MediatR.", StringComparison.Ordinal) => "Relevox." + name["MediatR.".Length..],
        _ => name,
    };

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
