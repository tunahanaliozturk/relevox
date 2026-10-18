using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace Capar.SourceGenerator;

/// <summary>
/// Decides what a type contributes: handler contracts, a concrete message, or a reason it was left out.
/// </summary>
/// <remarks>
/// The same rules apply to types declared in source and to types read from referenced assemblies, so a
/// handler behaves the same whichever project it lives in. That is the reason this is one class rather than
/// two passes that could drift apart.
/// </remarks>
internal static class Discovery
{
    private const string ContractsNamespace = "Capar";
    private const string PipelineNamespace = "Capar.Pipeline";
    private const string ContractsAssembly = "Capar.Contracts";

    private static readonly SymbolDisplayFormat PlainFormat = SymbolDisplayFormat.FullyQualifiedFormat;

    private static readonly SymbolDisplayFormat AnnotatedFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    public static DiscoveryModel Empty { get; } = new(
        EquatableArray<HandlerModel>.Empty,
        EquatableArray<MessageModel>.Empty,
        EquatableArray<SkippedModel>.Empty,
        EquatableArray<OpenRegistration>.Empty,
        EquatableArray<string>.Empty);

    public static DiscoveryModel Classify(
        INamedTypeSymbol symbol,
        Compilation compilation,
        bool fromSource,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (symbol.IsStatic || symbol.TypeKind is not (TypeKind.Class or TypeKind.Struct))
        {
            return Empty;
        }

        List<HandlerModel> handlers = [];
        List<MessageModel> messages = [];
        List<SkippedModel> skipped = [];
        List<OpenRegistration> open = [];
        HashSet<string> experimental = [];

        LocationModel? location = fromSource ? ToLocationModel(symbol) : null;
        bool concrete = !symbol.IsAbstract;
        bool accessible = IsAccessible(symbol, compilation);

        foreach (INamedTypeSymbol contract in symbol.AllInterfaces)
        {
            if (contract.ContainingAssembly?.Name != ContractsAssembly)
            {
                continue;
            }

            if (contract.ContainingNamespace?.ToDisplayString() == PipelineNamespace)
            {
                switch (contract.MetadataName)
                {
                    case "IRequestPreProcessor`1":
                        AddHandler(HandlerKind.PreProcessor, contract.TypeArguments[0], null, contract);
                        break;
                    case "IRequestPostProcessor`2":
                        AddHandler(HandlerKind.PostProcessor, contract.TypeArguments[0], contract.TypeArguments[1], contract);
                        break;
                    case "IRequestExceptionHandler`3":
                        AddHandler(HandlerKind.ExceptionHandler, contract.TypeArguments[0], contract.TypeArguments[1], contract, contract.TypeArguments[2]);
                        break;
                    case "IRequestExceptionAction`2":
                        AddHandler(HandlerKind.ExceptionAction, contract.TypeArguments[0], null, contract, contract.TypeArguments[1]);
                        break;
                    default:
                        break;
                }

                continue;
            }

            if (contract.ContainingNamespace?.ToDisplayString() != ContractsNamespace)
            {
                continue;
            }

            switch (contract.MetadataName)
            {
                case "IRequestHandler`2":
                    AddHandler(HandlerKind.Request, contract.TypeArguments[0], contract.TypeArguments[1], contract);
                    break;
                case "IRequestHandler`1":
                    AddHandler(HandlerKind.VoidRequest, contract.TypeArguments[0], null, contract);
                    break;
                case "INotificationHandler`1":
                    AddHandler(HandlerKind.Notification, contract.TypeArguments[0], null, contract);
                    break;
                case "IStreamRequestHandler`2":
                    AddHandler(HandlerKind.Stream, contract.TypeArguments[0], contract.TypeArguments[1], contract);
                    break;
                case "IRequest`1" or "IRequest":
                    AddMessage(symbol, MessageKind.Request, location, fromSource);
                    break;
                case "INotification":
                    AddMessage(symbol, MessageKind.Notification, location, fromSource);
                    break;
                case "IStreamRequest`1":
                    AddMessage(symbol, MessageKind.Stream, location, fromSource);
                    break;
                default:
                    break;
            }
        }

        return handlers.Count == 0 && messages.Count == 0 && skipped.Count == 0 && open.Count == 0
            ? Empty
            : new DiscoveryModel(
                handlers.Distinct().ToEquatableArray(),
                messages.Distinct().ToEquatableArray(),
                skipped.Distinct().ToEquatableArray(),
                open.Distinct().ToEquatableArray(),
                experimental.OrderBy(static id => id, System.StringComparer.Ordinal).ToEquatableArray());

        void AddHandler(HandlerKind kind, ITypeSymbol message, ITypeSymbol? response, INamedTypeSymbol contract, ITypeSymbol? exception = null)
        {
            if (!concrete || symbol.TypeKind != TypeKind.Class)
            {
                return;
            }

            // A generic processor whose type parameters are exactly the contract's, such as
            // LoggingPreProcessor<T> : IRequestPreProcessor<T>, applies to every request. It is registered open
            // and the container closes it, as MediatR's scan does. A generic handler has no such reading: it
            // would have to be closed over requests the generator cannot enumerate.
            if (symbol.IsGenericType
                && kind is HandlerKind.PreProcessor or HandlerKind.PostProcessor or HandlerKind.ExceptionHandler or HandlerKind.ExceptionAction
                && accessible
                && contract.TypeArguments.Length == symbol.TypeParameters.Length
                && contract.TypeArguments.Select((argument, index) => SymbolEqualityComparer.Default.Equals(argument, symbol.TypeParameters[index])).All(static same => same))
            {
                open.Add(new OpenRegistration(
                    contract.ConstructUnboundGenericType().ToDisplayString(PlainFormat),
                    symbol.ConstructUnboundGenericType().ToDisplayString(PlainFormat)));
                return;
            }

            if (symbol.IsGenericType)
            {
                if (fromSource)
                {
                    skipped.Add(Skip(Diagnostics.GenericHandler.Id, location, Display(symbol)));
                }

                return;
            }

            ITypeSymbol[] named = [symbol, message, .. response is null ? [] : new[] { response }, .. exception is null ? [] : new[] { exception }];

            if (!accessible || named.Any(type => !IsAccessible(type, compilation)))
            {
                string reason = fromSource
                    ? "is not accessible from the rest of this assembly"
                    : $"is internal to '{symbol.ContainingAssembly?.Name}'";
                skipped.Add(Skip(Diagnostics.Inaccessible.Id, location, Display(symbol), reason));
                return;
            }

            if (named.Any(IsObsoleteAsError))
            {
                skipped.Add(Skip(Diagnostics.Inaccessible.Id, location, Display(symbol), "or a type it handles is marked [Obsolete] as an error"));
                return;
            }

            foreach (ITypeSymbol type in named)
            {
                CollectExperimental(type, experimental);
            }

            handlers.Add(new HandlerModel(
                kind,
                Plain(symbol),
                Shape(message),
                response is null ? null : Plain(response),
                response is null ? null : AnnotatedName(response),
                exception is null ? EquatableArray<string>.Empty : Chain(exception),
                kind == HandlerKind.PostProcessor && response is not null ? AssignableTo(response) : EquatableArray<string>.Empty,
                symbol.DeclaredAccessibility == Accessibility.Public,
                // Worked out here rather than for every type with a base list: it walks the syntax trees.
                OrderOf(symbol, compilation, fromSource),
                location));

            // A notification handler for a closed generic type, such as INotificationHandler<Created<Order>>,
            // names a message no declaration produces: Created<T> itself is generic and skipped below. Without
            // this, Created<Order> would get no dispatch entry and publishing it would silently reach nothing.
            if (kind == HandlerKind.Notification
                && message is INamedTypeSymbol { IsGenericType: true, IsAbstract: false, TypeKind: TypeKind.Class or TypeKind.Struct } constructed)
            {
                AddMessage(constructed, MessageKind.Notification, null, declaredHere: false);
            }
        }

        void AddMessage(INamedTypeSymbol message, MessageKind kind, LocationModel? at, bool declaredHere)
        {
            // Only types that can actually be instantiated and sent get a dispatch entry. An abstract base or
            // an interface still matters for notifications, through the AssignableTo list of each concrete type
            // that derives from it. An open generic is reached through its closed forms, which handlers name.
            if (message.IsAbstract || message.IsUnboundGenericType || ContainsTypeParameters(message) || !IsAccessible(message, compilation))
            {
                return;
            }

            CollectExperimental(message, experimental);
            messages.Add(new MessageModel(kind, Shape(message), AssignableTo(message), declaredHere, at));
        }
    }

    /// <summary>
    /// Walks the referenced assemblies that can contain handlers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only assemblies that can see Capar.Contracts, directly or through another reference, can declare a
    /// handler, so everything else, the framework included, is skipped without being opened. The check is
    /// transitive because the compiler only records a reference to an assembly whose tokens are used: a
    /// handler deriving from a base handler in another project may carry no reference to the contracts at all.
    /// </para>
    /// <para>
    /// An assembly with its own generated mediator owns its handlers, so they are left to it. That is what lets
    /// a library use Çapar internally without its handlers leaking into every application that references it.
    /// Projects whose handlers belong to the application should reference Capar.Contracts, not Capar.
    /// </para>
    /// </remarks>
    public static DiscoveryModel ScanReferences(Compilation compilation, CancellationToken cancellationToken)
    {
        List<DiscoveryModel> found = [];
        Dictionary<IAssemblySymbol, bool> reachesContracts = new(SymbolEqualityComparer.Default);

        foreach (IAssemblySymbol assembly in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (assembly.Name is ContractsAssembly or "Capar" || !ReachesContracts(assembly, reachesContracts))
            {
                continue;
            }

            (DiscoveryModel[] parts, bool ownsMediator) = ScanAssembly(assembly, compilation, cancellationToken);

            if (!ownsMediator)
            {
                found.AddRange(parts);
                continue;
            }

            string[] shared =
            [
                .. parts.SelectMany(static part => part.Handlers)
                    .Where(static handler => handler.IsPublic)
                    .Select(static handler => Planner.Trim(handler.HandlerType))
                    .Distinct()
                    .OrderBy(static name => name, System.StringComparer.Ordinal),
            ];

            if (shared.Length > 0)
            {
                found.Add(Empty with
                {
                    Skipped = new[] { Skip(Diagnostics.OwnMediator.Id, null, assembly.Name, string.Join(", ", shared.Take(3)) + (shared.Length > 3 ? ", ..." : string.Empty)) }.ToEquatableArray(),
                });
            }
        }

        return Merge(found);
    }

    /// <summary>
    /// What one referenced assembly contributes, remembered for as long as Roslyn keeps the assembly's symbol.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this the walk over every type of every handler-bearing reference ran again on each keystroke:
    /// its output compared equal, so nothing downstream re-ran, but the walk itself did. Roslyn reuses the
    /// symbol of an unchanged metadata reference across compilations, so it is a natural cache key.
    /// </para>
    /// <para>
    /// The result also depends on the consuming assembly, through InternalsVisibleTo, and on the reference's
    /// position, which orders its handlers. Several projects in one solution consume the same reference at
    /// once, so each symbol keeps one entry per consumer and position rather than a single slot they would
    /// keep overwriting. The generator and the analyzer can scan at the same moment, so every access holds the
    /// entry's lock; the M4 review found that two unguarded misses could make the second one throw.
    /// </para>
    /// </remarks>
    private static (DiscoveryModel[] Parts, bool OwnsMediator) ScanAssembly(IAssemblySymbol assembly, Compilation compilation, CancellationToken cancellationToken)
    {
        string consumer = compilation.Assembly.Identity.ToString();
        int position = 0;
        foreach (IAssemblySymbol referenced in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            if (SymbolEqualityComparer.Default.Equals(referenced, assembly))
            {
                break;
            }

            position++;
        }

        ScanEntries entries = ScanCache.GetValue(assembly, static _ => new ScanEntries());
        string key = consumer + "|" + position.ToString(System.Globalization.CultureInfo.InvariantCulture);
        lock (entries)
        {
            if (entries.TryGetValue(key, out (DiscoveryModel[] Parts, bool OwnsMediator) cached))
            {
                return cached;
            }
        }

        List<DiscoveryModel> parts = [];
        bool ownsMediator = false;
        foreach (INamedTypeSymbol type in AllTypes(assembly.GlobalNamespace))
        {
            ownsMediator |= IsGeneratedMediator(type);
            DiscoveryModel model = Classify(type, compilation, fromSource: false, cancellationToken);
            if (!model.IsEmpty)
            {
                parts.Add(model);
            }
        }

        (DiscoveryModel[] Parts, bool OwnsMediator) result = ([.. parts], ownsMediator);
        lock (entries)
        {
            entries[key] = result;
        }

        return result;
    }

    private static readonly ConditionalWeakTable<IAssemblySymbol, ScanEntries> ScanCache = new();

    /// <summary>One cached scan per consumer and position. Guarded by locking the instance.</summary>
    private sealed class ScanEntries : Dictionary<string, (DiscoveryModel[] Parts, bool OwnsMediator)>
    {
        public ScanEntries()
            : base(System.StringComparer.Ordinal)
        {
        }
    }

    public static DiscoveryModel Merge(IEnumerable<DiscoveryModel> parts)
    {
        List<DiscoveryModel> list = [.. parts];
        return new DiscoveryModel(
            list.SelectMany(static part => part.Handlers).Distinct().ToEquatableArray(),
            list.SelectMany(static part => part.Messages).Distinct().ToEquatableArray(),
            list.SelectMany(static part => part.Skipped).Distinct().ToEquatableArray(),
            list.SelectMany(static part => part.Open).Distinct().ToEquatableArray(),
            list.SelectMany(static part => part.Experimental).Distinct().OrderBy(static id => id, System.StringComparer.Ordinal).ToEquatableArray());
    }

    private static bool ReachesContracts(IAssemblySymbol assembly, Dictionary<IAssemblySymbol, bool> memo)
    {
        if (memo.TryGetValue(assembly, out bool known))
        {
            return known;
        }

        // Guards against reference cycles, which metadata allows: a cycle that has not found the contracts
        // yet is treated as not reaching them, and the real answer is filled in on the way back out.
        memo[assembly] = false;
        bool reaches = assembly.Name == ContractsAssembly
            || assembly.Modules.Any(module => module.ReferencedAssemblySymbols.Any(referenced => ReachesContracts(referenced, memo)));
        memo[assembly] = reaches;
        return reaches;
    }

    /// <summary>
    /// Whether a referenced assembly's generated <c>AddCapar</c> sits in the shared namespace and is visible
    /// here through InternalsVisibleTo. One metadata lookup per reference that grants access; nothing is walked.
    /// </summary>
    public static bool SeesAnotherRegistration(Compilation compilation)
    {
        foreach (IAssemblySymbol assembly in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            if (assembly.GivesAccessTo(compilation.Assembly)
                && assembly.GetTypeByMetadataName("Microsoft.Extensions.DependencyInjection.CaparServiceCollectionExtensions") is { } registration
                && IsGeneratedByCapar(registration))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsGeneratedMediator(INamedTypeSymbol type) => type.Name == "CaparMediator" && IsGeneratedByCapar(type);

    public static bool IsGeneratedByCapar(INamedTypeSymbol type) =>
        type.GetAttributes().Any(static attribute =>
            attribute.AttributeClass?.ToDisplayString() == "System.CodeDom.Compiler.GeneratedCodeAttribute"
            && attribute.ConstructorArguments.Length > 0
            && attribute.ConstructorArguments[0].Value is "Capar");

    private static IEnumerable<INamedTypeSymbol> AllTypes(INamespaceSymbol root)
    {
        Stack<INamespaceOrTypeSymbol> pending = new();
        pending.Push(root);

        while (pending.Count > 0)
        {
            INamespaceOrTypeSymbol current = pending.Pop();

            foreach (ISymbol member in current.GetMembers())
            {
                if (member is INamespaceSymbol ns)
                {
                    pending.Push(ns);
                }
                else if (member is INamedTypeSymbol type)
                {
                    yield return type;
                    pending.Push(type);
                }
            }
        }
    }

    private static MessageShape Shape(ITypeSymbol type) => new(
        Plain(type),
        AnnotatedName(type),
        type.IsValueType,
        type.IsValueType || type.IsSealed,
        type.IsAbstract || type.TypeKind == TypeKind.Interface);

    /// <summary>
    /// Whether generated code in this compilation can name the type: the type itself, every type it is nested
    /// in, and every type argument.
    /// </summary>
    private static bool IsAccessible(ITypeSymbol type, Compilation compilation)
    {
        for (ITypeSymbol? current = type; current is not null; current = current.ContainingType)
        {
            if (current is INamedTypeSymbol { IsFileLocal: true })
            {
                return false;
            }

            if (current is INamedTypeSymbol named && named.TypeArguments.Any(argument => argument.TypeKind != TypeKind.TypeParameter && !IsAccessible(argument, compilation)))
            {
                return false;
            }
        }

        return type is IArrayTypeSymbol array
            ? IsAccessible(array.ElementType, compilation)
            : compilation.IsSymbolAccessibleWithin(type, compilation.Assembly);
    }

    private static bool ContainsTypeParameters(ITypeSymbol type) =>
        type.TypeKind == TypeKind.TypeParameter
        || (type is INamedTypeSymbol named && named.TypeArguments.Any(ContainsTypeParameters));

    private static bool IsObsoleteAsError(ITypeSymbol type)
    {
        for (ITypeSymbol? current = type; current is not null; current = current.ContainingType)
        {
            foreach (AttributeData attribute in current.GetAttributes())
            {
                if (attribute.AttributeClass?.ToDisplayString() == "System.ObsoleteAttribute"
                    && attribute.ConstructorArguments.Length == 2
                    && attribute.ConstructorArguments[1].Value is true)
                {
                    return true;
                }
            }

            if (current is INamedTypeSymbol named && named.TypeArguments.Any(IsObsoleteAsError))
            {
                return true;
            }
        }

        return false;
    }

    private static void CollectExperimental(ITypeSymbol type, HashSet<string> ids)
    {
        for (ITypeSymbol? current = type; current is not null; current = current.ContainingType)
        {
            foreach (AttributeData attribute in current.GetAttributes())
            {
                if (attribute.AttributeClass?.ToDisplayString() == "System.Diagnostics.CodeAnalysis.ExperimentalAttribute"
                    && attribute.ConstructorArguments.Length > 0
                    && attribute.ConstructorArguments[0].Value is string id
                    && id.Length > 0)
                {
                    ids.Add(id);
                }
            }

            if (current is INamedTypeSymbol named)
            {
                foreach (ITypeSymbol argument in named.TypeArguments)
                {
                    CollectExperimental(argument, ids);
                }
            }
        }
    }

    private static EquatableArray<string> AssignableTo(ITypeSymbol symbol)
    {
        if (symbol.IsValueType)
        {
            return new[] { Plain(symbol) }.ToEquatableArray();
        }

        List<string> names = [];
        for (ITypeSymbol? current = symbol; current is not null; current = current.BaseType)
        {
            names.Add(Plain(current));
        }

        names.AddRange(symbol.AllInterfaces.Select(static contract => Plain(contract)));
        return names.Distinct().ToEquatableArray();
    }

    private static DeclarationOrder OrderOf(INamedTypeSymbol symbol, Compilation compilation, bool fromSource)
    {
        if (!fromSource)
        {
            int assembly = 1;
            foreach (IAssemblySymbol referenced in compilation.SourceModule.ReferencedAssemblySymbols)
            {
                if (SymbolEqualityComparer.Default.Equals(referenced, symbol.ContainingAssembly))
                {
                    break;
                }

                assembly++;
            }

            return new DeclarationOrder(assembly, string.Empty, 0, symbol.MetadataToken);
        }

        // A nested type is ordered by the top-level type it is declared in.
        INamedTypeSymbol outer = symbol;
        while (outer.ContainingType is { } containing)
        {
            outer = containing;
        }

        string ns = outer.ContainingNamespace is { IsGlobalNamespace: false } containingNamespace
            ? containingNamespace.ToDisplayString()
            : string.Empty;

        Location? location = symbol.Locations.FirstOrDefault(static candidate => candidate.IsInSource);
        int tree = int.MaxValue;
        if (location?.SourceTree is { } declared)
        {
            int index = 0;
            foreach (SyntaxTree candidate in compilation.SyntaxTrees)
            {
                if (candidate == declared)
                {
                    tree = index;
                    break;
                }

                index++;
            }
        }

        return new DeclarationOrder(0, ns, tree, location?.SourceSpan.Start ?? 0);
    }

    private static EquatableArray<string> Chain(ITypeSymbol type)
    {
        List<string> names = [];
        for (ITypeSymbol? current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            names.Add(Plain(current));
        }

        return names.ToEquatableArray();
    }

    private static SkippedModel Skip(string rule, LocationModel? location, params string[] arguments) =>
        new(rule, arguments.ToEquatableArray(), location);

    private static string Plain(ITypeSymbol symbol) => symbol.ToDisplayString(PlainFormat);

    private static string AnnotatedName(ITypeSymbol symbol) => symbol.ToDisplayString(AnnotatedFormat);

    private static string Display(ITypeSymbol symbol) =>
        symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);

    private static LocationModel? ToLocationModel(ISymbol symbol)
    {
        Location? location = symbol.Locations.FirstOrDefault(static candidate => candidate.IsInSource);
        if (location is null)
        {
            return null;
        }

        FileLinePositionSpan span = location.GetLineSpan();
        return new LocationModel(
            location.SourceTree?.FilePath ?? string.Empty,
            location.SourceSpan.Start,
            location.SourceSpan.Length,
            span.StartLinePosition.Line,
            span.StartLinePosition.Character,
            span.EndLinePosition.Line,
            span.EndLinePosition.Character);
    }
}
