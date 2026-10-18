using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Capar.SourceGenerator;

/// <summary>
/// Closes generic handlers at build time, so the generated code can call them like any other handler.
/// </summary>
/// <remarks>
/// <para>
/// MediatR registers a generic notification handler such as <c>Audit&lt;T&gt; : INotificationHandler&lt;T&gt;</c>
/// as an open generic, and the container closes it for every notification published, within its constraints.
/// That is MediatR's default, so it is matched: the handler is closed over every notification type the build
/// can see.
/// </para>
/// <para>
/// A generic request or stream handler, such as <c>GetByIdHandler&lt;T&gt; : IRequestHandler&lt;GetById&lt;T&gt;, string&gt;
/// where T : IEntity</c>, is registered by MediatR only with <c>RegisterGenericHandlers</c>, which closes it
/// over every type in the scanned assemblies that satisfies the constraints. The generator does the same, from
/// the types in this compilation and the references that can see the constraint, and only for type parameters
/// constrained to a type declared in the solution: an unconstrained one would mean closing it over everything.
/// The generator cannot read the run-time option, so it does this whether or not the option was set; the
/// difference is that a send MediatR would have failed for want of a handler now works.
/// </para>
/// <para>
/// Every closing goes through <see cref="Classify"/> like a handler someone wrote, so it is held to the same
/// rules, and the planner gives a written handler precedence over a closed one.
/// </para>
/// </remarks>
internal static partial class Discovery
{
    /// <summary>More closings than this for one generic handler is reported rather than generated.</summary>
    private const int MaxClosings = 256;

    // Strong-name keys of the .NET runtime and Microsoft's libraries. A constraint naming a type from one of
    // these, such as IComparable, does not bound the search: everything implements it.
    private static readonly HashSet<string> FrameworkKeys = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "b77a5c561934e089",
        "b03f5f7f11d50a3a",
        "31bf3856ad364e35",
        "cc7b13ffcd2ddd51",
        "adb9793829ddae60",
        "7cec85d7bea7798e",
    };

    private static readonly ConditionalWeakTable<IAssemblySymbol, INamedTypeSymbol[]> TypeCache = new();

    public static DiscoveryModel CloseGenerics(Compilation compilation, CancellationToken cancellationToken)
    {
        if (compilation.GetTypeByMetadataName("Capar.IBaseRequest") is null)
        {
            return Empty;
        }

        List<INamedTypeSymbol> types = [.. AllTypes(compilation.Assembly.GlobalNamespace)];
        Dictionary<IAssemblySymbol, bool> reachesContracts = new(SymbolEqualityComparer.Default);
        foreach (IAssemblySymbol assembly in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            if (assembly.Name is ContractsAssembly or "Capar" || !ReachesContracts(assembly, reachesContracts))
            {
                continue;
            }

            // A reference with its own mediator keeps its handlers, generic ones included.
            if (!ScanAssembly(assembly, compilation, cancellationToken).OwnsMediator)
            {
                types.AddRange(TypesOf(assembly));
            }
        }

        INamedTypeSymbol[] definitions = [.. types.Where(type => IsOpen(type) && HandlerContracts(type).Any())];
        if (definitions.Length == 0)
        {
            return Empty;
        }

        INamedTypeSymbol[] notifications = NotificationsIn(types, compilation);
        List<DiscoveryModel> parts = [];
        foreach (INamedTypeSymbol definition in definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? problem = Close(definition, compilation, notifications, parts, cancellationToken);
            if (problem is not null && definition.Locations.Any(static location => location.IsInSource))
            {
                parts.Add(Empty with
                {
                    Skipped = new[] { Skip(Diagnostics.GenericHandler.Id, ToLocationModel(definition), Display(definition), problem) }.ToEquatableArray(),
                });
            }
        }

        return Merge(parts);
    }

    /// <summary>Closes one generic handler, or says why it cannot be closed.</summary>
    private static string? Close(
        INamedTypeSymbol definition,
        Compilation compilation,
        INamedTypeSymbol[] notifications,
        List<DiscoveryModel> parts,
        CancellationToken cancellationToken)
    {
        INamedTypeSymbol[] contracts = [.. HandlerContracts(definition)];

        if (definition.IsAbstract || definition.TypeKind != TypeKind.Class)
        {
            return null;
        }

        if (contracts.All(static contract => contract.ContainingNamespace?.ToDisplayString() == PipelineNamespace))
        {
            // Registered open by Classify when its type parameters are exactly the interface's.
            return IsOpenProcessorShape(definition, contracts)
                ? null
                : "a generic processor, exception handler or exception action is registered only when its type parameters are exactly the interface's";
        }

        if (definition.ContainingType is not null && IsOpen(definition.ContainingType))
        {
            return "it is nested in a generic type";
        }

        if (contracts.Any(static contract => contract.MetadataName == "INotificationHandler`1"))
        {
            if (definition.TypeParameters.Length != 1
                || contracts.Length != 1
                || !SymbolEqualityComparer.Default.Equals(contracts[0].TypeArguments[0], definition.TypeParameters[0]))
            {
                return "a generic notification handler is closed only when it is declared as H<T> : INotificationHandler<T>";
            }

            foreach (INamedTypeSymbol notification in notifications)
            {
                Dictionary<ITypeParameterSymbol, ITypeSymbol> map = new(SymbolEqualityComparer.Default) { [definition.TypeParameters[0]] = notification };
                if (Satisfies(definition.TypeParameters[0], notification, map, compilation))
                {
                    parts.Add(Classify(definition.Construct(notification), compilation, fromSource: false, cancellationToken, closedFrom: definition));
                }
            }

            return null;
        }

        // A request or stream handler: every type parameter needs a constraint that bounds the search.
        List<INamedTypeSymbol[]> choices = [];
        foreach (ITypeParameterSymbol parameter in definition.TypeParameters)
        {
            ITypeSymbol[] bounds = [.. parameter.ConstraintTypes.Where(bound => IsSolutionType(bound, compilation))];
            if (bounds.Length == 0)
            {
                return $"its type parameter '{parameter.Name}' has no constraint naming a type declared in this solution, so there is nothing to close it over";
            }

            // Filtered here when the constraints stand alone, which keeps the product small; a constraint that
            // mentions another type parameter, as in where T : IEntity<TKey>, is checked once the product is built.
            bool standalone = !parameter.ConstraintTypes.Any(ContainsTypeParameters);
            INamedTypeSymbol[] candidates =
            [
                .. CandidatesFor(bounds, compilation)
                    .Where(candidate => !standalone || Satisfies(parameter, candidate, new Dictionary<ITypeParameterSymbol, ITypeSymbol>(SymbolEqualityComparer.Default) { [parameter] = candidate }, compilation)),
            ];

            choices.Add(candidates);
        }

        long combinations = choices.Aggregate(1L, static (product, candidates) => product * candidates.Length);
        if (combinations == 0)
        {
            return "no type in this solution satisfies its constraints";
        }

        if (combinations > MaxClosings)
        {
            return $"its constraints are satisfied by {combinations} type combinations, more than the {MaxClosings} the generator will write";
        }

        int closed = 0;
        foreach (INamedTypeSymbol[] arguments in Product(choices))
        {
            Dictionary<ITypeParameterSymbol, ITypeSymbol> map = new(SymbolEqualityComparer.Default);
            for (int index = 0; index < arguments.Length; index++)
            {
                map[definition.TypeParameters[index]] = arguments[index];
            }

            if (definition.TypeParameters.All(parameter => Satisfies(parameter, map[parameter], map, compilation)))
            {
                parts.Add(Classify(definition.Construct(arguments), compilation, fromSource: false, cancellationToken, closedFrom: definition));
                closed++;
            }
        }

        return closed == 0 ? "no type in this solution satisfies its constraints" : null;
    }

    private static IEnumerable<INamedTypeSymbol> HandlerContracts(INamedTypeSymbol type) =>
        type.AllInterfaces.Where(static contract =>
            contract.ContainingAssembly?.Name == ContractsAssembly
            && contract.MetadataName is "IRequestHandler`2" or "IRequestHandler`1" or "INotificationHandler`1" or "IStreamRequestHandler`2"
                or "IRequestPreProcessor`1" or "IRequestPostProcessor`2" or "IRequestExceptionHandler`3" or "IRequestExceptionAction`2");

    private static bool IsOpenProcessorShape(INamedTypeSymbol definition, INamedTypeSymbol[] contracts) =>
        contracts.Any(contract =>
            contract.TypeArguments.Length == definition.TypeParameters.Length
            && contract.TypeArguments.Select((argument, index) => SymbolEqualityComparer.Default.Equals(argument, definition.TypeParameters[index])).All(static same => same));

    /// <summary>
    /// The notification types the build can see: concrete ones declared anywhere a handler can be, and closed
    /// generic ones that a handler names.
    /// </summary>
    private static INamedTypeSymbol[] NotificationsIn(List<INamedTypeSymbol> types, Compilation compilation)
    {
        HashSet<INamedTypeSymbol> found = new(SymbolEqualityComparer.Default);
        foreach (INamedTypeSymbol type in types)
        {
            if (IsOpen(type))
            {
                continue;
            }

            foreach (INamedTypeSymbol contract in type.AllInterfaces)
            {
                if (contract.ContainingAssembly?.Name != ContractsAssembly)
                {
                    continue;
                }

                if (contract.MetadataName == "INotification" && IsSendable(type, compilation))
                {
                    found.Add(type);
                }
                else if (contract.MetadataName == "INotificationHandler`1"
                    && contract.TypeArguments[0] is INamedTypeSymbol { IsGenericType: true } named
                    && IsSendable(named, compilation))
                {
                    found.Add(named);
                }
            }
        }

        return [.. found];
    }

    private static bool IsSendable(INamedTypeSymbol type, Compilation compilation) =>
        type is { IsAbstract: false, IsStatic: false, TypeKind: TypeKind.Class or TypeKind.Struct }
        && !IsOpen(type)
        && type.CanBeReferencedByName
        && IsAccessible(type, compilation);

    /// <summary>A type declared in this compilation, or in a referenced assembly that is not part of .NET.</summary>
    private static bool IsSolutionType(ITypeSymbol bound, Compilation compilation)
    {
        if (bound is not INamedTypeSymbol || bound.ContainingAssembly is not { } assembly)
        {
            return false;
        }

        if (SymbolEqualityComparer.Default.Equals(assembly, compilation.Assembly))
        {
            return true;
        }

        string key = string.Concat(assembly.Identity.PublicKeyToken.Select(static part => part.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));
        return !FrameworkKeys.Contains(key);
    }

    /// <summary>
    /// Types that can stand in for a type parameter bounded by these constraints: those in this compilation,
    /// and in every reference that can see one of the constraints' assemblies.
    /// </summary>
    private static IEnumerable<INamedTypeSymbol> CandidatesFor(ITypeSymbol[] bounds, Compilation compilation)
    {
        IAssemblySymbol[] targets = [.. bounds.Select(static bound => bound.ContainingAssembly).Distinct(SymbolEqualityComparer.Default).Cast<IAssemblySymbol>()];

        List<INamedTypeSymbol> types = [.. AllTypes(compilation.Assembly.GlobalNamespace)];
        foreach (IAssemblySymbol assembly in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            Dictionary<IAssemblySymbol, bool> memo = new(SymbolEqualityComparer.Default);
            if (targets.Any(target => Reaches(assembly, target, memo)))
            {
                types.AddRange(TypesOf(assembly));
            }
        }

        return types.Where(type => IsSendable(type, compilation));
    }

    private static bool Reaches(IAssemblySymbol assembly, IAssemblySymbol target, Dictionary<IAssemblySymbol, bool> memo)
    {
        if (memo.TryGetValue(assembly, out bool known))
        {
            return known;
        }

        memo[assembly] = false;
        bool reaches = SymbolEqualityComparer.Default.Equals(assembly, target)
            || assembly.Modules.Any(module => module.ReferencedAssemblySymbols.Any(referenced => Reaches(referenced, target, memo)));
        memo[assembly] = reaches;
        return reaches;
    }

    /// <summary>Whether a type argument meets a type parameter's constraints, as the compiler would check them.</summary>
    private static bool Satisfies(ITypeParameterSymbol parameter, ITypeSymbol argument, Dictionary<ITypeParameterSymbol, ITypeSymbol> map, Compilation compilation)
    {
        if (parameter.HasReferenceTypeConstraint && !argument.IsReferenceType)
        {
            return false;
        }

        if (parameter.HasValueTypeConstraint
            && (!argument.IsValueType || argument.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T))
        {
            return false;
        }

        if (parameter.HasUnmanagedTypeConstraint && !argument.IsUnmanagedType)
        {
            return false;
        }

        if (parameter.HasConstructorConstraint
            && !argument.IsValueType
            && !(argument is INamedTypeSymbol { IsAbstract: false } named
                && named.InstanceConstructors.Any(static constructor => constructor.Parameters.Length == 0 && constructor.DeclaredAccessibility == Accessibility.Public)))
        {
            return false;
        }

        foreach (ITypeSymbol constraint in parameter.ConstraintTypes)
        {
            ITypeSymbol? target = Substitute(constraint, map);
            if (target is null)
            {
                return false;
            }

            Conversion conversion = ((CSharpCompilation)compilation).ClassifyConversion(argument, target);
            if (!(conversion.IsIdentity || (conversion.IsImplicit && (conversion.IsReference || conversion.IsBoxing))))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Replaces type parameters in a constraint, as in <c>where T : IEntity&lt;T&gt;</c>, or null if one is unknown.</summary>
    private static ITypeSymbol? Substitute(ITypeSymbol type, Dictionary<ITypeParameterSymbol, ITypeSymbol> map)
    {
        if (type is ITypeParameterSymbol parameter)
        {
            return map.TryGetValue(parameter, out ITypeSymbol? argument) ? argument : null;
        }

        if (type is INamedTypeSymbol { IsGenericType: true } named && named.TypeArguments.Any(ContainsTypeParameters))
        {
            ITypeSymbol[] arguments = new ITypeSymbol[named.TypeArguments.Length];
            for (int index = 0; index < arguments.Length; index++)
            {
                if (Substitute(named.TypeArguments[index], map) is not { } substituted)
                {
                    return null;
                }

                arguments[index] = substituted;
            }

            return named.OriginalDefinition.Construct(arguments);
        }

        return type;
    }

    private static IEnumerable<INamedTypeSymbol[]> Product(List<INamedTypeSymbol[]> choices)
    {
        int[] at = new int[choices.Count];
        while (true)
        {
            yield return [.. choices.Select((candidates, index) => candidates[at[index]])];

            int position = choices.Count - 1;
            while (position >= 0 && ++at[position] == choices[position].Length)
            {
                at[position] = 0;
                position--;
            }

            if (position < 0)
            {
                yield break;
            }
        }
    }

    /// <summary>Every type in a referenced assembly, walked once for as long as Roslyn keeps the assembly.</summary>
    private static INamedTypeSymbol[] TypesOf(IAssemblySymbol assembly) =>
        TypeCache.GetValue(assembly, static key => [.. AllTypes(key.GlobalNamespace)]);
}
