using System.Collections.Generic;
using System.Linq;
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

    public static DiscoveryModel Classify(
        INamedTypeSymbol symbol,
        Compilation compilation,
        bool fromSource,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        List<HandlerModel> handlers = [];
        List<MessageModel> messages = [];
        List<SkippedModel> skipped = [];

        if (symbol.IsStatic || symbol.TypeKind is not (TypeKind.Class or TypeKind.Struct))
        {
            return Empty;
        }

        LocationModel? location = fromSource ? ToLocationModel(symbol) : null;
        bool concrete = !symbol.IsAbstract;
        bool generic = symbol.IsGenericType;
        bool accessible = IsAccessible(symbol, compilation);

        foreach (INamedTypeSymbol contract in symbol.AllInterfaces)
        {
            if (contract.ContainingNamespace?.ToDisplayString() != ContractsNamespace)
            {
                continue;
            }

            switch (contract.MetadataName)
            {
                case "IRequestHandler`2":
                    AddHandler(HandlerKind.Request, contract.TypeArguments[0], contract.TypeArguments[1]);
                    break;
                case "IRequestHandler`1":
                    AddHandler(HandlerKind.VoidRequest, contract.TypeArguments[0], null);
                    break;
                case "INotificationHandler`1":
                    AddHandler(HandlerKind.Notification, contract.TypeArguments[0], null);
                    break;
                case "IStreamRequestHandler`2":
                    AddHandler(HandlerKind.Stream, contract.TypeArguments[0], contract.TypeArguments[1]);
                    break;
                case "IRequest`1" or "IRequest":
                    AddMessage(MessageKind.Request);
                    break;
                case "INotification":
                    AddMessage(MessageKind.Notification);
                    break;
                case "IStreamRequest`1":
                    AddMessage(MessageKind.Stream);
                    break;
                default:
                    break;
            }
        }

        return handlers.Count == 0 && messages.Count == 0 && skipped.Count == 0
            ? Empty
            : new DiscoveryModel(
                handlers.Distinct().ToEquatableArray(),
                messages.Distinct().ToEquatableArray(),
                skipped.Distinct().ToEquatableArray());

        void AddHandler(HandlerKind kind, ITypeSymbol message, ITypeSymbol? response)
        {
            if (!concrete || symbol.TypeKind != TypeKind.Class)
            {
                return;
            }

            if (generic)
            {
                if (fromSource)
                {
                    skipped.Add(new SkippedModel(Diagnostics.GenericHandler.Id, Display(symbol), string.Empty, location));
                }

                return;
            }

            if (!accessible
                || !IsAccessible(message, compilation)
                || (response is not null && !IsAccessible(response, compilation)))
            {
                string reason = fromSource
                    ? "is not accessible from the rest of this assembly"
                    : $"is not accessible outside '{symbol.ContainingAssembly?.Name}'";
                skipped.Add(new SkippedModel(Diagnostics.Inaccessible.Id, Display(symbol), reason, location));
                return;
            }

            handlers.Add(new HandlerModel(
                kind,
                FullName(symbol),
                FullName(message),
                response is null ? null : FullName(response),
                message.IsValueType,
                location));
        }

        void AddMessage(MessageKind kind)
        {
            // Only types that can actually be instantiated and sent get a dispatch entry. An abstract base or
            // an interface still matters for notifications, through the AssignableTo list of each concrete
            // type that derives from it.
            if (!concrete || generic || !accessible)
            {
                return;
            }

            messages.Add(new MessageModel(
                kind,
                FullName(symbol),
                symbol.IsValueType,
                AssignableTo(symbol),
                fromSource,
                location));
        }
    }

    public static DiscoveryModel Empty { get; } = new(
        EquatableArray<HandlerModel>.Empty,
        EquatableArray<MessageModel>.Empty,
        EquatableArray<SkippedModel>.Empty);

    /// <summary>
    /// Walks the referenced assemblies that can contain handlers.
    /// </summary>
    /// <remarks>
    /// Only assemblies that reference Capar.Contracts can declare a handler or a message, so everything else,
    /// the framework included, is skipped without being opened. That keeps the walk proportional to the
    /// user's own projects rather than to the whole dependency graph.
    /// </remarks>
    public static DiscoveryModel ScanReferences(Compilation compilation, CancellationToken cancellationToken)
    {
        List<DiscoveryModel> found = [];

        foreach (IAssemblySymbol assembly in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (assembly.Name == "Capar.Contracts" || !ReferencesContracts(assembly))
            {
                continue;
            }

            foreach (INamedTypeSymbol type in AllTypes(assembly.GlobalNamespace))
            {
                DiscoveryModel model = Classify(type, compilation, fromSource: false, cancellationToken);
                if (!model.IsEmpty)
                {
                    found.Add(model);
                }
            }
        }

        return Merge(found);
    }

    public static DiscoveryModel Merge(IEnumerable<DiscoveryModel> parts)
    {
        List<DiscoveryModel> list = [.. parts];
        return new DiscoveryModel(
            list.SelectMany(static part => part.Handlers).Distinct().ToEquatableArray(),
            list.SelectMany(static part => part.Messages).Distinct().ToEquatableArray(),
            list.SelectMany(static part => part.Skipped).Distinct().ToEquatableArray());
    }

    private static bool ReferencesContracts(IAssemblySymbol assembly) =>
        assembly.Modules.Any(static module =>
            module.ReferencedAssemblies.Any(static identity => identity.Name == "Capar.Contracts"));

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

    private static bool IsAccessible(ITypeSymbol type, Compilation compilation) =>
        type is not INamedTypeSymbol { IsFileLocal: true }
        && compilation.IsSymbolAccessibleWithin(type, compilation.Assembly);

    private static EquatableArray<string> AssignableTo(INamedTypeSymbol symbol)
    {
        List<string> names = [];

        for (INamedTypeSymbol? current = symbol; current is not null; current = current.BaseType)
        {
            names.Add(FullName(current));
        }

        names.AddRange(symbol.AllInterfaces.Select(FullName));
        return names.Distinct().ToEquatableArray();
    }

    private static string FullName(ITypeSymbol symbol) =>
        symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

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
