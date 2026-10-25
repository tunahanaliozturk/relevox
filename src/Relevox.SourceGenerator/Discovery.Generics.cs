using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Relevox.SourceGenerator;

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
/// over every type in the scanned assemblies that satisfies the constraints. The generator does the same over
/// the solution's own assemblies, and only for type parameters constrained to a type declared in one of them:
/// an unconstrained one would mean closing it over everything.
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

    /// <summary>Type combinations past this are not even tried, so a wide product cannot stall the IDE.</summary>
    private const int MaxCombinations = 65536;

    private static readonly string[] NotificationContract = ["INotificationHandler`1"];

    private static readonly string[] RequestContracts = ["IRequestHandler`2", "IRequestHandler`1", "IStreamRequestHandler`2"];

    // Strong-name keys of .NET and of Microsoft's libraries, the Azure SDK included.
    private static readonly HashSet<string> MicrosoftKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "b77a5c561934e089",
        "b03f5f7f11d50a3a",
        "31bf3856ad364e35",
        "cc7b13ffcd2ddd51",
        "adb9793829ddae60",
        "7cec85d7bea7798e",
        "92742159e12e44c8",
    };

    private static readonly ConditionalWeakTable<IAssemblySymbol, TypeFacts> FactsCache = new();

    public static DiscoveryModel CloseGenerics(Compilation compilation, CancellationToken cancellationToken)
    {
        if (compilation.GetTypeByMetadataName("Relevox.IBaseRequest") is null)
        {
            return Empty;
        }

        Closer closer = new(compilation, cancellationToken);
        DiscoveryModel closed = closer.Definitions.Count == 0 ? Empty : closer.Run();
        (EquatableArray<string> definitions, EquatableArray<ValidationRegistration> validations) = closer.Validations();
        return definitions.Length == 0 ? closed : closed with { ValidationDefinitions = definitions, Validations = validations };
    }

    /// <summary>
    /// Whether a referenced assembly declares a generic handler this project could close. Classifying a type
    /// skips these, since they only become handlers once closed, so they are looked for separately.
    /// </summary>
    private static bool HasGenericHandlers(IAssemblySymbol assembly, Compilation compilation) =>
        FactsCache.GetValue(assembly, static key => TypeFacts.Of(AllTypes(key.GlobalNamespace))).Definitions
            .Any(definition => compilation.IsSymbolAccessibleWithin(definition, compilation.Assembly));

    private static IEnumerable<INamedTypeSymbol> HandlerContracts(INamedTypeSymbol type) =>
        type.AllInterfaces.Where(static contract =>
            contract.ContainingAssembly?.Name == ContractsAssembly
            && contract.MetadataName is "IRequestHandler`2" or "IRequestHandler`1" or "INotificationHandler`1" or "IStreamRequestHandler`2"
                or "IRequestPreProcessor`1" or "IRequestPostProcessor`2" or "IRequestExceptionHandler`3" or "IRequestExceptionAction`2");

    /// <summary>One run of the closing step: what it found, worked out once and shared by every definition.</summary>
    private sealed class Closer
    {
        private readonly Compilation _compilation;
        private readonly CancellationToken _cancellationToken;
        private readonly TypeFacts _source;
        private readonly List<IAssemblySymbol> _solution = [];
        private readonly Dictionary<IAssemblySymbol, INamedTypeSymbol[]> _pools = new(SymbolEqualityComparer.Default);
        private readonly Dictionary<string, INamedTypeSymbol[]> _fitting = new(StringComparer.Ordinal);
        private readonly List<DiscoveryModel> _parts = [];
        private INamedTypeSymbol[]? _notifications;

        public Closer(Compilation compilation, CancellationToken cancellationToken)
        {
            _compilation = compilation;
            _cancellationToken = cancellationToken;
            _source = TypeFacts.Of(AllTypes(compilation.Assembly.GlobalNamespace));
            Definitions.AddRange(_source.Definitions);

            Dictionary<IAssemblySymbol, bool> reachesContracts = new(SymbolEqualityComparer.Default);
            foreach (IAssemblySymbol assembly in compilation.SourceModule.ReferencedAssemblySymbols)
            {
                if (IsPackage(assembly, compilation))
                {
                    continue;
                }

                _solution.Add(assembly);
                if (assembly.Name is ContractsAssembly or "Relevox" || !ReachesContracts(assembly, reachesContracts))
                {
                    continue;
                }

                // A reference with its own mediator keeps its handlers, generic ones included.
                if (!ScanAssembly(assembly, compilation, cancellationToken).OwnsMediator)
                {
                    TypeFacts facts = FactsCache.GetValue(assembly, static key => TypeFacts.Of(AllTypes(key.GlobalNamespace)));
                    Definitions.AddRange(facts.Definitions);
                    HandlerAssemblies.Add(facts);
                }
            }
        }

        public List<INamedTypeSymbol> Definitions { get; } = [];

        private List<TypeFacts> HandlerAssemblies { get; } = [];

        public DiscoveryModel Run()
        {
            foreach (INamedTypeSymbol definition in Definitions)
            {
                _cancellationToken.ThrowIfCancellationRequested();

                if (Close(definition) is ({ } rule, { } reason)
                    && (definition.Locations.Any(static location => location.IsInSource) || rule == Diagnostics.Inaccessible.Id))
                {
                    _parts.Add(Empty with
                    {
                        Skipped = new[] { Skip(rule, ToLocationModel(definition), Display(definition), reason) }.ToEquatableArray(),
                    });
                }
            }

            ExceptionFamily[] families = [.. Families()];
            if (families.Length > 0)
            {
                _parts.Add(Empty with { ExceptionFamilies = families.ToEquatableArray() });
            }

            return Merge(_parts);
        }

        /// <summary>
        /// For each class an open exception handler or action is constrained to, the classes below it that the
        /// solution declares. MediatR closes such a handler over every class in a thrown exception's chain that
        /// meets the constraint; the generated catalog can only ask about classes it names, so it names these.
        /// Classes from packages, .NET's included, are left out, as they are for generic handlers.
        /// </summary>
        private IEnumerable<ExceptionFamily> Families()
        {
            HashSet<INamedTypeSymbol> bounds = new(SymbolEqualityComparer.Default);
            foreach (INamedTypeSymbol definition in Definitions)
            {
                foreach (INamedTypeSymbol contract in HandlerContracts(definition))
                {
                    if (contract.MetadataName is "IRequestExceptionHandler`3" or "IRequestExceptionAction`2"
                        && IsOpenProcessorShape(definition, [contract])
                        && contract.TypeArguments[contract.TypeArguments.Length - 1] is ITypeParameterSymbol thrown
                        && thrown.ConstraintTypes.Where(static bound => bound.ToDisplayString() != "System.Exception").ToArray() is { Length: 1 } narrower
                        && narrower[0] is INamedTypeSymbol { TypeKind: TypeKind.Class } bound
                        && !ContainsTypeParameters(bound)
                        && IsAccessible(bound, _compilation))
                    {
                        bounds.Add(bound);
                    }
                }
            }

            foreach (INamedTypeSymbol bound in bounds)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                Dictionary<IAssemblySymbol, bool> memo = new(SymbolEqualityComparer.Default);
                IEnumerable<INamedTypeSymbol> exceptions = _source.Exceptions;
                foreach (IAssemblySymbol assembly in _solution)
                {
                    if (bound.ContainingAssembly is { } home && Reaches(assembly, home, memo))
                    {
                        exceptions = exceptions.Concat(FactsCache.GetValue(assembly, static key => TypeFacts.Of(AllTypes(key.GlobalNamespace))).Exceptions);
                    }
                }

                string[] subclasses =
                [
                    .. exceptions
                        .Where(type => !SymbolEqualityComparer.Default.Equals(type, bound) && Derives(type, bound) && IsAccessible(type, _compilation))
                        .Select(Plain)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(static name => name, StringComparer.Ordinal),
                ];
                if (subclasses.Length > 0)
                {
                    yield return new ExceptionFamily(Plain(bound), subclasses.ToEquatableArray());
                }
            }

            static bool Derives(INamedTypeSymbol type, INamedTypeSymbol bound)
            {
                for (INamedTypeSymbol? current = type.BaseType; current is not null; current = current.BaseType)
                {
                    if (SymbolEqualityComparer.Default.Equals(current, bound))
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        /// <summary>
        /// Relevox.FluentValidation's behaviour, and the open classes derived from it, each closed over every request
        /// the solution declares a validator for (ADR 0020). Nothing without the package.
        /// </summary>
        /// <remarks>
        /// A validator counts for the exact type it validates: the container resolves <c>IValidator&lt;TRequest&gt;</c>
        /// without variance, so a validator for a base type never runs for a derived request, here or in the
        /// behaviour MediatR applications write.
        /// </remarks>
        public (EquatableArray<string> Definitions, EquatableArray<ValidationRegistration> Validations) Validations()
        {
            if (_compilation.GetTypeByMetadataName("Relevox.FluentValidation.ValidationBehavior`2") is not { } behavior
                || _compilation.GetTypeByMetadataName("FluentValidation.IValidator`1") is not { } validator)
            {
                return (EquatableArray<string>.Empty, EquatableArray<ValidationRegistration>.Empty);
            }

            INamedTypeSymbol[] types = [.. AllTypes(_compilation.Assembly.GlobalNamespace), .. _solution.SelectMany(static assembly => AllTypes(assembly.GlobalNamespace))];

            INamedTypeSymbol[] definitions =
            [
                behavior,
                .. types.Where(type => type is { TypeKind: TypeKind.Class, IsAbstract: false, TypeParameters.Length: 2 }
                    && IsOpen(type)
                    && !SymbolEqualityComparer.Default.Equals(type, behavior)
                    && IsAccessible(type, _compilation)
                    && Derives(type, behavior)),
            ];

            HashSet<INamedTypeSymbol> validated = new(SymbolEqualityComparer.Default);
            INamedTypeSymbol[]? requests = null;
            foreach (INamedTypeSymbol type in types)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (type is not { TypeKind: TypeKind.Class, IsAbstract: false })
                {
                    continue;
                }

                foreach (INamedTypeSymbol contract in type.AllInterfaces)
                {
                    if (!SymbolEqualityComparer.Default.Equals(contract.OriginalDefinition, validator))
                    {
                        continue;
                    }

                    if (!IsOpen(type))
                    {
                        if (contract.TypeArguments[0] is INamedTypeSymbol { IsAbstract: false } request)
                        {
                            validated.Add(request);
                        }

                        continue;
                    }

                    // A generic validator is registered by hand, closed over whatever the application chose, which the
                    // build cannot see. One shaped like RequiredFields<T> : AbstractValidator<T> counts for every
                    // request its constraints allow; any other shape for every request, so that no validator an
                    // application registered goes unrun. Either way a request it does not cover pays for an empty
                    // list of validators, as it would have without the package.
                    requests ??= [.. types.Where(candidate => candidate is { IsAbstract: false } && !IsOpen(candidate) && IsAccessible(candidate, _compilation) && ResponseOf(candidate) is not null)];
                    bool simple = type.TypeParameters.Length == 1 && SymbolEqualityComparer.Default.Equals(contract.TypeArguments[0], type.TypeParameters[0]);
                    validated.UnionWith(simple ? requests.Where(request => Discovery.Close(type, [request], _compilation) is not null) : requests);
                }
            }

            List<ValidationRegistration> registrations = [];
            foreach (INamedTypeSymbol request in validated.Where(type => IsAccessible(type, _compilation)))
            {
                if (ResponseOf(request) is not { } response)
                {
                    continue;
                }

                foreach (INamedTypeSymbol definition in definitions)
                {
                    if (Discovery.Close(definition, [request, response], _compilation) is { } closed)
                    {
                        registrations.Add(new ValidationRegistration(
                            Unbound(definition),
                            $"global::Relevox.IPipelineBehavior<{Plain(request)}, {Plain(response)}>",
                            Plain(closed),
                            Plain(request)));
                    }
                }
            }

            return (
                definitions.Select(Unbound).ToEquatableArray(),
                registrations.OrderBy(static registration => registration.ImplementationType, StringComparer.Ordinal).ToEquatableArray());

            ITypeSymbol? ResponseOf(INamedTypeSymbol request)
            {
                foreach (INamedTypeSymbol contract in request.AllInterfaces)
                {
                    if (contract.ContainingAssembly?.Name == ContractsAssembly && contract.MetadataName == "IRequest`1")
                    {
                        return contract.TypeArguments[0];
                    }
                }

                return request.AllInterfaces.Any(static contract => contract.ContainingAssembly?.Name == ContractsAssembly && contract.MetadataName == "IRequest")
                    ? _compilation.GetTypeByMetadataName("Relevox.Unit")
                    : null;
            }

            static string Unbound(INamedTypeSymbol definition) => definition.ConstructUnboundGenericType().ToDisplayString(PlainFormat);

            static bool Derives(INamedTypeSymbol type, INamedTypeSymbol definition)
            {
                for (INamedTypeSymbol? current = type.BaseType; current is not null; current = current.BaseType)
                {
                    if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, definition))
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        /// <summary>Closes one generic handler, or says which rule it falls foul of and why.</summary>
        private (string Rule, string Reason)? Close(INamedTypeSymbol definition)
        {
            if (definition.IsAbstract || definition.TypeKind != TypeKind.Class)
            {
                return null;
            }

            if (!IsAccessible(definition, _compilation))
            {
                bool here = SymbolEqualityComparer.Default.Equals(definition.ContainingAssembly, _compilation.Assembly);
                return (Diagnostics.Inaccessible.Id, here ? "is not accessible from the rest of this assembly" : $"is internal to '{definition.ContainingAssembly?.Name}'");
            }

            INamedTypeSymbol[] contracts = [.. HandlerContracts(definition)];
            INamedTypeSymbol[] pipeline = [.. contracts.Where(static contract => contract.ContainingNamespace?.ToDisplayString() == PipelineNamespace)];
            INamedTypeSymbol[] handlers = [.. contracts.Except(pipeline, SymbolEqualityComparer.Default).Cast<INamedTypeSymbol>()];

            // A processor whose type parameters are exactly the interface's is registered open by Classify.
            if (pipeline.Length > 0 && !IsOpenProcessorShape(definition, pipeline))
            {
                return (Diagnostics.GenericHandler.Id, "a generic processor, exception handler or exception action is registered only when its type parameters are exactly the interface's");
            }

            if (handlers.Length == 0)
            {
                return null;
            }

            if (definition.ContainingType is not null && IsOpen(definition.ContainingType))
            {
                return (Diagnostics.GenericHandler.Id, "it is nested in a generic type");
            }

            // A notification handler for its own type parameter is closed as one, whatever else the class does,
            // as the container closes only the interface it was asked for.
            if (definition.TypeParameters.Length == 1
                && handlers.Any(contract => contract.MetadataName == "INotificationHandler`1"
                    && SymbolEqualityComparer.Default.Equals(contract.TypeArguments[0], definition.TypeParameters[0])))
            {
                ITypeParameterSymbol parameter = definition.TypeParameters[0];

                // Kept open too, for notification types the build never saw, such as EntityCreated<Invoice> built
                // in a generic base class: the container would close it for those under MediatR. Not when its type
                // parameter asks the trimmer to keep members, which a type closed at run time cannot promise.
                if (!parameter.GetAttributes().Any(static attribute => attribute.AttributeClass?.Name == "DynamicallyAccessedMembersAttribute"))
                {
                    _parts.Add(Empty with
                    {
                        OpenNotifications = new[]
                        {
                            new OpenNotificationHandler(
                                definition.ConstructUnboundGenericType().ToDisplayString(PlainFormat),
                                OrderOf(definition, _compilation, definition.Locations.Any(static location => location.IsInSource))),
                        }.ToEquatableArray(),
                    });
                }

                foreach (INamedTypeSymbol notification in Notifications())
                {
                    if (Satisfies(parameter, notification, Map(parameter, notification), _compilation))
                    {
                        _parts.Add(Classify(definition.Construct(notification), _compilation, fromSource: false, _cancellationToken, definition, NotificationContract));
                    }
                }

                return null;
            }

            if (handlers.All(static contract => contract.MetadataName == "INotificationHandler`1"))
            {
                return (Diagnostics.GenericHandler.Id, "a generic notification handler is closed only when it is declared as H<T> : INotificationHandler<T>, with one type parameter");
            }

            return CloseRequestHandler(definition);
        }

        private (string Rule, string Reason)? CloseRequestHandler(INamedTypeSymbol definition)
        {
            List<INamedTypeSymbol[]> choices = [];
            foreach (ITypeParameterSymbol parameter in definition.TypeParameters)
            {
                IAssemblySymbol[] targets = [.. BoundAssemblies(parameter, [])];
                if (targets.Length == 0)
                {
                    return (Diagnostics.GenericHandler.Id, $"its type parameter '{parameter.Name}' has no constraint naming a type declared in this solution, so there is nothing to close it over");
                }

                choices.Add(Fitting(parameter, targets));
            }

            long product = 1;
            foreach (INamedTypeSymbol[] candidates in choices)
            {
                product = Math.Min(product * candidates.Length, MaxCombinations + 1L);
            }

            if (product > MaxCombinations)
            {
                return (Diagnostics.GenericHandler.Id, $"its constraints are satisfied by more than the {MaxClosings} type combinations the generator will write");
            }

            List<INamedTypeSymbol[]> closings = [];
            int count = 0;
            if (product > 0)
            {
                foreach (INamedTypeSymbol[] arguments in Product(choices))
                {
                    Dictionary<ITypeParameterSymbol, ITypeSymbol> map = new(SymbolEqualityComparer.Default);
                    for (int index = 0; index < arguments.Length; index++)
                    {
                        map[definition.TypeParameters[index]] = arguments[index];
                    }

                    if (definition.TypeParameters.All(parameter => Satisfies(parameter, map[parameter], map, _compilation)))
                    {
                        closings.Add(arguments);
                        if (++count > MaxClosings)
                        {
                            break;
                        }
                    }
                }
            }

            if (count == 0)
            {
                return (Diagnostics.GenericHandler.Id, "no type in this solution satisfies its constraints");
            }

            if (count > MaxClosings)
            {
                return (Diagnostics.GenericHandler.Id, $"its constraints are satisfied by more than the {MaxClosings} type combinations the generator will write");
            }

            foreach (INamedTypeSymbol[] arguments in closings)
            {
                _parts.Add(Classify(definition.Construct(arguments), _compilation, fromSource: false, _cancellationToken, definition, RequestContracts));
            }

            return null;
        }

        /// <summary>
        /// The assemblies a type parameter's constraints point into, when they are the solution's own; empty when
        /// nothing bounds the parameter. A constraint on another type parameter borrows that one's bounds.
        /// </summary>
        private IEnumerable<IAssemblySymbol> BoundAssemblies(ITypeParameterSymbol parameter, HashSet<ITypeParameterSymbol> seen)
        {
            if (!seen.Add(parameter))
            {
                yield break;
            }

            foreach (ITypeSymbol bound in parameter.ConstraintTypes)
            {
                if (bound is ITypeParameterSymbol other)
                {
                    foreach (IAssemblySymbol assembly in BoundAssemblies(other, seen))
                    {
                        yield return assembly;
                    }
                }
                else if (bound is INamedTypeSymbol { ContainingAssembly: { } assembly }
                    && (SymbolEqualityComparer.Default.Equals(assembly, _compilation.Assembly) || _solution.Contains(assembly, SymbolEqualityComparer.Default)))
                {
                    yield return assembly;
                }
            }
        }

        /// <summary>
        /// The candidates that pass <see cref="Fits"/>, worked out once per set of constraints: generic handlers
        /// in one solution tend to share them, as every <c>where T : IEntity</c> does.
        /// </summary>
        private INamedTypeSymbol[] Fitting(ITypeParameterSymbol parameter, IAssemblySymbol[] targets)
        {
            string key = string.Join(
                "|",
                [
                    parameter.HasReferenceTypeConstraint ? "class" : string.Empty,
                    parameter.HasValueTypeConstraint ? "struct" : string.Empty,
                    parameter.HasUnmanagedTypeConstraint ? "unmanaged" : string.Empty,
                    parameter.HasConstructorConstraint ? "new" : string.Empty,
                    .. parameter.ConstraintTypes.Select(static bound => bound is ITypeParameterSymbol ? "T" : bound.OriginalDefinition.ToDisplayString(PlainFormat) + (bound is INamedTypeSymbol named && IsOpen(named) ? "(open)" : bound.ToDisplayString(PlainFormat))),
                    .. targets.Select(static target => target.Identity.ToString()),
                ]);

            if (!_fitting.TryGetValue(key, out INamedTypeSymbol[]? fitting))
            {
                fitting = [.. targets.SelectMany(Pool).Distinct(SymbolEqualityComparer.Default).Cast<INamedTypeSymbol>().Where(candidate => Fits(parameter, candidate))];
                _fitting[key] = fitting;
            }

            return fitting;
        }

        /// <summary>Types that could stand in for a parameter bounded by a type from this assembly.</summary>
        private INamedTypeSymbol[] Pool(IAssemblySymbol target)
        {
            if (_pools.TryGetValue(target, out INamedTypeSymbol[]? pool))
            {
                return pool;
            }

            Dictionary<IAssemblySymbol, bool> memo = new(SymbolEqualityComparer.Default);
            IEnumerable<INamedTypeSymbol> found = _source.Candidates;
            foreach (IAssemblySymbol assembly in _solution)
            {
                if (Reaches(assembly, target, memo))
                {
                    found = found.Concat(FactsCache.GetValue(assembly, static key => TypeFacts.Of(AllTypes(key.GlobalNamespace))).Candidates);
                }
            }

            pool = [.. found.Where(candidate => IsAccessible(candidate, _compilation))];
            _pools[target] = pool;
            return pool;
        }

        /// <summary>
        /// A cheap first filter on one type parameter: its special constraints, constraints that stand alone, and
        /// the shape of a constraint like <c>IEntity&lt;T&gt;</c>. The full check runs once every argument is known.
        /// </summary>
        private bool Fits(ITypeParameterSymbol parameter, INamedTypeSymbol candidate)
        {
            if (!SatisfiesSpecial(parameter, candidate))
            {
                return false;
            }

            foreach (ITypeSymbol bound in parameter.ConstraintTypes)
            {
                bool open = bound is ITypeParameterSymbol || (bound is INamedTypeSymbol named && IsOpen(named));
                if (!open)
                {
                    if (!Converts(candidate, bound, _compilation))
                    {
                        return false;
                    }
                }
                else if (bound is INamedTypeSymbol shape
                    && !candidate.AllInterfaces.Concat(BaseTypes(candidate)).Any(type => SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, shape.OriginalDefinition)))
                {
                    return false;
                }
            }

            return true;
        }

        private INamedTypeSymbol[] Notifications() =>
            _notifications ??=
            [
                .. HandlerAssemblies.SelectMany(static facts => facts.Notifications)
                    .Concat(_source.Notifications)
                    .Distinct(SymbolEqualityComparer.Default)
                    .Cast<INamedTypeSymbol>()
                    .Where(notification => IsAccessible(notification, _compilation)),
            ];

        private static Dictionary<ITypeParameterSymbol, ITypeSymbol> Map(ITypeParameterSymbol parameter, ITypeSymbol argument) =>
            new(SymbolEqualityComparer.Default) { [parameter] = argument };

        private static IEnumerable<INamedTypeSymbol> BaseTypes(INamedTypeSymbol type)
        {
            for (INamedTypeSymbol? current = type.BaseType; current is not null; current = current.BaseType)
            {
                yield return current;
            }
        }
    }

    /// <summary>
    /// What one assembly offers the closing step. Nothing here depends on who is asking, so a referenced
    /// assembly's facts are cached; accessibility, which does, is checked at use.
    /// </summary>
    /// <remarks>
    /// Only the generic handler definitions are worked out up front, and only generic types are asked for their
    /// interfaces to find them. Notifications and candidates need every type's interfaces, which on a project of
    /// thousands of types is most of the step's cost, so they wait until a generic handler needs them: a project
    /// without one pays for a walk over its type names and nothing else.
    /// </remarks>
    private sealed class TypeFacts
    {
        private readonly Lazy<INamedTypeSymbol[]> _notifications;
        private readonly Lazy<INamedTypeSymbol[]> _candidates;
        private readonly Lazy<INamedTypeSymbol[]> _exceptions;

        private TypeFacts(INamedTypeSymbol[] types)
        {
            Definitions = [.. types.Where(static type => IsOpen(type) && HandlerContracts(type).Any())];
            _notifications = new(() => NotificationsIn(types));
            _exceptions = new(() =>
            [
                .. types.Where(static type => !IsOpen(type) && type is { TypeKind: TypeKind.Class, CanBeReferencedByName: true } && IsException(type)),
            ]);
            _candidates = new(() =>
            [
                .. types.Where(static type => !IsOpen(type) && type is { IsAbstract: false, IsStatic: false, TypeKind: TypeKind.Class or TypeKind.Struct, CanBeReferencedByName: true }),
            ]);
        }

        /// <summary>Generic handler classes.</summary>
        public INamedTypeSymbol[] Definitions { get; }

        /// <summary>Concrete notification types, and closed generic ones that a handler names.</summary>
        public INamedTypeSymbol[] Notifications => _notifications.Value;

        /// <summary>Types that could be a type argument of a sent request: concrete, closed and nameable.</summary>
        public INamedTypeSymbol[] Candidates => _candidates.Value;

        /// <summary>Exception classes, abstract ones included: closed and nameable.</summary>
        public INamedTypeSymbol[] Exceptions => _exceptions.Value;

        public static TypeFacts Of(IEnumerable<INamedTypeSymbol> types) => new([.. types]);

        private static bool IsException(INamedTypeSymbol type)
        {
            for (INamedTypeSymbol? current = type.BaseType; current is not null; current = current.BaseType)
            {
                if (current is { Name: "Exception", ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true } })
                {
                    return true;
                }
            }

            return false;
        }

        private static INamedTypeSymbol[] NotificationsIn(INamedTypeSymbol[] types)
        {
            HashSet<INamedTypeSymbol> notifications = new(SymbolEqualityComparer.Default);
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

                    if (contract.MetadataName == "INotification" && type is { IsAbstract: false, TypeKind: TypeKind.Class or TypeKind.Struct, CanBeReferencedByName: true })
                    {
                        notifications.Add(type);
                    }
                    // A notification a handler names counts even when its own assembly is not walked here, such as
                    // a library with a mediator of its own: the handler gives it a dispatch entry, and that entry
                    // has to carry the generic handlers' closings too.
                    else if (contract.MetadataName == "INotificationHandler`1"
                        && contract.TypeArguments[0] is INamedTypeSymbol { IsAbstract: false, TypeKind: TypeKind.Class or TypeKind.Struct } named
                        && !IsOpen(named))
                    {
                        notifications.Add(named);
                    }
                }
            }

            return [.. notifications];
        }
    }

    private static bool IsOpenProcessorShape(INamedTypeSymbol definition, INamedTypeSymbol[] contracts) =>
        contracts.Any(contract =>
            contract.TypeArguments.Length == definition.TypeParameters.Length
            && contract.TypeArguments.Select((argument, index) => SymbolEqualityComparer.Default.Equals(argument, definition.TypeParameters[index])).All(static same => same));

    /// <summary>
    /// An assembly that is part of .NET or came from a package rather than from the solution: a constraint
    /// naming one of its types does not bound the search, and its types are not candidates.
    /// </summary>
    private static bool IsPackage(IAssemblySymbol assembly, Compilation compilation)
    {
        string key = string.Concat(assembly.Identity.PublicKeyToken.Select(static part => part.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));
        if (MicrosoftKeys.Contains(key))
        {
            return true;
        }

        string path = (compilation.GetMetadataReference(assembly) as PortableExecutableReference)?.FilePath?.Replace('\\', '/') ?? string.Empty;
        return path.IndexOf("/.nuget/packages/", StringComparison.OrdinalIgnoreCase) >= 0
            || path.IndexOf("/packs/", StringComparison.OrdinalIgnoreCase) >= 0;
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

    /// <summary>
    /// A generic definition closed over <paramref name="arguments"/> in order, as the container closes an open
    /// registration, or null when that breaks a constraint and the container would leave it out.
    /// </summary>
    internal static INamedTypeSymbol? Close(INamedTypeSymbol definition, ITypeSymbol[] arguments, Compilation compilation)
    {
        if (definition.TypeParameters.Length != arguments.Length)
        {
            return null;
        }

        Dictionary<ITypeParameterSymbol, ITypeSymbol> map = new(SymbolEqualityComparer.Default);
        for (int index = 0; index < arguments.Length; index++)
        {
            map[definition.TypeParameters[index]] = arguments[index];
        }

        return definition.TypeParameters.All(parameter => Satisfies(parameter, map[parameter], map, compilation))
            ? definition.Construct(arguments)
            : null;
    }

    /// <summary>Whether a type argument meets a type parameter's constraints, as the compiler would check them.</summary>
    private static bool Satisfies(ITypeParameterSymbol parameter, ITypeSymbol argument, Dictionary<ITypeParameterSymbol, ITypeSymbol> map, Compilation compilation)
    {
        if (!SatisfiesSpecial(parameter, argument))
        {
            return false;
        }

        foreach (ITypeSymbol constraint in parameter.ConstraintTypes)
        {
            if (Substitute(constraint, map) is not { } target || !Converts(argument, target, compilation))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SatisfiesSpecial(ITypeParameterSymbol parameter, ITypeSymbol argument)
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

        return !parameter.HasConstructorConstraint
            || argument.IsValueType
            || (argument is INamedTypeSymbol { IsAbstract: false } named
                && named.InstanceConstructors.Any(static constructor => constructor.Parameters.Length == 0 && constructor.DeclaredAccessibility == Accessibility.Public));
    }

    // The conversions a type argument may use to meet a constraint: identity, implicit reference, boxing. The
    // common case, a class or interface the argument inherits, is answered from its base types and interfaces;
    // the compiler's conversion check, which also knows about variance, is the fallback.
    private static bool Converts(ITypeSymbol argument, ITypeSymbol target, Compilation compilation)
    {
        if (SymbolEqualityComparer.Default.Equals(argument, target) || argument.AllInterfaces.Contains(target, SymbolEqualityComparer.Default))
        {
            return true;
        }

        for (ITypeSymbol? current = argument.BaseType; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, target))
            {
                return true;
            }
        }

        // Without variance, inheriting is the only way to a class or interface, and that was checked above.
        if (target is INamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Interface } named
            && named.TypeParameters.All(static parameter => parameter.Variance == VarianceKind.None))
        {
            return false;
        }

        Conversion conversion = ((CSharpCompilation)compilation).ClassifyConversion(argument, target);
        return conversion.IsIdentity || (conversion.IsImplicit && (conversion.IsReference || conversion.IsBoxing));
    }

    /// <summary>
    /// Replaces type parameters in a constraint, as in <c>where T : IEntity&lt;T&gt;</c> or
    /// <c>where T : Outer&lt;T&gt;.IInner</c>, or null if one is unknown.
    /// </summary>
    private static ITypeSymbol? Substitute(ITypeSymbol type, Dictionary<ITypeParameterSymbol, ITypeSymbol> map)
    {
        if (type is ITypeParameterSymbol parameter)
        {
            return map.TryGetValue(parameter, out ITypeSymbol? argument) ? argument : null;
        }

        if (type is not INamedTypeSymbol named)
        {
            return type;
        }

        bool openOuter = named.ContainingType is { } outer && IsOpen(outer);
        if (!openOuter && !named.TypeArguments.Any(ContainsTypeParameters))
        {
            return type;
        }

        ITypeSymbol[] arguments = new ITypeSymbol[named.TypeArguments.Length];
        for (int index = 0; index < arguments.Length; index++)
        {
            if (Substitute(named.TypeArguments[index], map) is not { } substituted)
            {
                return null;
            }

            arguments[index] = substituted;
        }

        INamedTypeSymbol definition = named.OriginalDefinition;
        if (openOuter)
        {
            if (Substitute(named.ContainingType!, map) is not INamedTypeSymbol closedContaining
                || closedContaining.GetTypeMembers(named.Name, named.Arity).FirstOrDefault() is not { } member)
            {
                return null;
            }

            definition = member;
        }

        return arguments.Length == 0 ? definition : definition.Construct(arguments);
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
}
