using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Relevox.Migration;

/// <summary>
/// Says how much is left on MediatR while the bridge runs the two side by side, and what the bridge cannot do
/// (#132, ADR 0022).
/// </summary>
/// <remarks>
/// <para>
/// RVX0014 and RVX0016 are reported at the <c>AddMediatRBridge</c> call, so they appear once, in the project that
/// owns the bridge. They count the project's own types and those of the projects it references. A project
/// reference is usually compiled against the other project's reference assembly, which leaves out internal types
/// unless InternalsVisibleTo grants them, so an internal handler in another project may not be counted.
/// </para>
/// <para>
/// RVX0015 is a warning because a request that has moved silently loses whatever a MediatR-only behaviour did:
/// logging, authorisation, a transaction. It fires only once something has moved: a message in the project carries
/// both libraries' interfaces, or the project references the bridge and has a Relevox handler. No build without
/// the bridge gets it, and neither does the first step of a migration, which adds the bridge and moves nothing.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BridgeAnalyzer : DiagnosticAnalyzer
{
    private const string BridgeAssembly = "Relevox.MediatRBridge";

    /// <summary>What is still on MediatR.</summary>
    public static readonly DiagnosticDescriptor StillOnMediatR = new(
        id: "RVX0014",
        title: "Code still on MediatR",
        messageFormat: "Still on MediatR, in this project and the projects it references: handlers {0}, behaviours {1}, processors {2}",
        category: "Relevox",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        customTags: [WellKnownDiagnosticTags.CompilationEnd],
        helpLinkUri: "https://github.com/tunahanaliozturk/relevox/blob/main/docs/diagnostics.md#rvx0014",
        description: "Counts the handlers, and the behaviours and processors without Relevox's interface, that still run on MediatR while the bridge is in place.");

    /// <summary>A MediatR behaviour or processor that moved requests skip.</summary>
    public static readonly DiagnosticDescriptor SkippedByMoved = new(
        id: "RVX0015",
        title: "A MediatR behaviour does not run for requests moved to Relevox",
        messageFormat: "'{0}' runs only for requests MediatR handles; requests moved to Relevox skip it. Implement Relevox's {1} as well and register it with AddRelevox.",
        category: "Relevox",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        customTags: [WellKnownDiagnosticTags.CompilationEnd],
        helpLinkUri: "https://github.com/tunahanaliozturk/relevox/blob/main/docs/diagnostics.md#rvx0015",
        description: "Through the bridge each request runs the pipeline of the library that handles it. A behaviour or processor with only MediatR's interface never sees a request that has moved.");

    /// <summary>Nothing left on MediatR.</summary>
    public static readonly DiagnosticDescriptor BridgeCanGo = new(
        id: "RVX0016",
        title: "Nothing is left on MediatR",
        messageFormat: "No handler in this project or the projects it references is left on MediatR; remove AddMediatRBridge, AddMediatR and the MediatR package",
        category: "Relevox",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        customTags: [WellKnownDiagnosticTags.CompilationEnd],
        helpLinkUri: "https://github.com/tunahanaliozturk/relevox/blob/main/docs/diagnostics.md#rvx0016",
        description: "Every handler the build can see is on Relevox, so the bridge sends nothing to MediatR any more.");

    // MediatR's handler interfaces.
    private static readonly string[] Handlers =
    [
        "MediatR.IRequestHandler`1",
        "MediatR.IRequestHandler`2",
        "MediatR.INotificationHandler`1",
        "MediatR.IStreamRequestHandler`2",
    ];

    // MediatR's pipeline interfaces, each with Relevox's counterpart and whether it is a behaviour or a processor.
    private static readonly (string MediatR, string Relevox, bool Behavior)[] Pipeline =
    [
        ("MediatR.IPipelineBehavior`2", "Relevox.IPipelineBehavior`2", true),
        ("MediatR.IStreamPipelineBehavior`2", "Relevox.IStreamPipelineBehavior`2", true),
        ("MediatR.Pipeline.IRequestPreProcessor`1", "Relevox.Pipeline.IRequestPreProcessor`1", false),
        ("MediatR.Pipeline.IRequestPostProcessor`2", "Relevox.Pipeline.IRequestPostProcessor`2", false),
        ("MediatR.Pipeline.IRequestExceptionHandler`3", "Relevox.Pipeline.IRequestExceptionHandler`3", false),
        ("MediatR.Pipeline.IRequestExceptionAction`2", "Relevox.Pipeline.IRequestExceptionAction`2", false),
    ];

    // Message interfaces: a type with one from each list has moved while keeping MediatR's, which only the bridge needs.
    private static readonly string[] MediatRMessages = ["MediatR.IBaseRequest", "MediatR.INotification", "MediatR.IStreamRequest`1"];
    private static readonly string[] RelevoxMessages = ["Relevox.IBaseRequest", "Relevox.INotification", "Relevox.IStreamRequest`1"];

    private static readonly string[] RelevoxHandlers =
    [
        "Relevox.IRequestHandler`1",
        "Relevox.IRequestHandler`2",
        "Relevox.INotificationHandler`1",
        "Relevox.IStreamRequestHandler`2",
    ];

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [StillOnMediatR, SkippedByMoved, BridgeCanGo];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static start =>
        {
            if (Types.Read(start.Compilation) is not { } types)
            {
                return;
            }

            ConcurrentBag<(INamedTypeSymbol Type, Classification Found)> own = [];
            ConcurrentBag<Location> calls = [];
            int dual = 0;
            int relevoxHandler = 0;

            start.RegisterSymbolAction(
                symbol =>
                {
                    INamedTypeSymbol type = (INamedTypeSymbol)symbol.Symbol;
                    if (types.Classify(type) is { } found)
                    {
                        own.Add((type, found));
                    }

                    if (types.CarriesBoth(type))
                    {
                        _ = Interlocked.Exchange(ref dual, 1);
                    }

                    if (types.HandlesOnRelevox(type))
                    {
                        _ = Interlocked.Exchange(ref relevoxHandler, 1);
                    }
                },
                SymbolKind.NamedType);

            start.RegisterOperationAction(
                operation =>
                {
                    IMethodSymbol method = ((IInvocationOperation)operation.Operation).TargetMethod;
                    if (method is { Name: "AddMediatRBridge", ContainingType.Name: "MediatRBridgeServiceCollectionExtensions" }
                        && method.ContainingAssembly.Name == BridgeAssembly)
                    {
                        calls.Add(operation.Operation.Syntax.GetLocation());
                    }
                },
                OperationKind.Invocation);

            start.RegisterCompilationEndAction(end =>
            {
                bool inUse = Volatile.Read(ref dual) == 1
                    || (Volatile.Read(ref relevoxHandler) == 1
                        && end.Compilation.ReferencedAssemblyNames.Any(static identity => identity.Name == BridgeAssembly));
                if (inUse)
                {
                    foreach ((INamedTypeSymbol type, Classification found) in own.OrderBy(static pair => pair.Type.Locations[0].SourceTree?.FilePath, StringComparer.Ordinal).ThenBy(static pair => pair.Type.Locations[0].SourceSpan.Start))
                    {
                        if (found.Unmoved is { } missing)
                        {
                            end.ReportDiagnostic(Diagnostic.Create(SkippedByMoved, type.Locations[0], type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), missing));
                        }
                    }
                }

                if (calls.IsEmpty)
                {
                    return;
                }

                List<Classification> all = [.. own.Select(static pair => pair.Found), .. types.InReferences(end.Compilation, end.CancellationToken)];
                int handlers = all.Count(static found => found.Kind == Kind.Handler);
                int behaviors = all.Count(static found => found.Kind == Kind.Behavior);
                int processors = all.Count(static found => found.Kind == Kind.Processor);

                foreach (Location call in calls.OrderBy(static call => call.SourceTree?.FilePath, StringComparer.Ordinal).ThenBy(static call => call.SourceSpan.Start))
                {
                    end.ReportDiagnostic(handlers == 0
                        ? Diagnostic.Create(BridgeCanGo, call)
                        : Diagnostic.Create(StillOnMediatR, call, handlers, behaviors, processors));
                }
            });
        });
    }

    private enum Kind
    {
        Handler,
        Behavior,
        Processor,
    }

    /// <summary>A type on MediatR, and for a behaviour or processor the Relevox interface it lacks.</summary>
    private sealed class Classification(Kind kind, string? unmoved)
    {
        public Kind Kind { get; } = kind;

        public string? Unmoved { get; } = unmoved;
    }

    private sealed class Types
    {
        private readonly HashSet<INamedTypeSymbol> _handlers;
        private readonly (INamedTypeSymbol MediatR, INamedTypeSymbol? Relevox, bool Behavior)[] _pipeline;
        private readonly HashSet<INamedTypeSymbol> _mediatrMessages;
        private readonly HashSet<INamedTypeSymbol> _relevoxMessages;
        private readonly HashSet<INamedTypeSymbol> _relevoxHandlers;

        private Types(
            HashSet<INamedTypeSymbol> handlers,
            (INamedTypeSymbol, INamedTypeSymbol?, bool)[] pipeline,
            HashSet<INamedTypeSymbol> mediatrMessages,
            HashSet<INamedTypeSymbol> relevoxMessages,
            HashSet<INamedTypeSymbol> relevoxHandlers)
        {
            _handlers = handlers;
            _pipeline = pipeline;
            _mediatrMessages = mediatrMessages;
            _relevoxMessages = relevoxMessages;
            _relevoxHandlers = relevoxHandlers;
        }

        /// <summary>Null unless the project sees both MediatR and Relevox, the only case with anything to say.</summary>
        public static Types? Read(Compilation compilation)
        {
            HashSet<INamedTypeSymbol> handlers = Resolve(compilation, Handlers);
            HashSet<INamedTypeSymbol> relevoxMessages = Resolve(compilation, RelevoxMessages);
            if (handlers.Count == 0 || relevoxMessages.Count == 0)
            {
                return null;
            }

            (INamedTypeSymbol, INamedTypeSymbol?, bool)[] pipeline =
            [
                .. Pipeline
                    .Select(entry => (MediatR: compilation.GetTypeByMetadataName(entry.MediatR), Relevox: compilation.GetTypeByMetadataName(entry.Relevox), entry.Behavior))
                    .Where(static entry => entry.MediatR is not null)
                    .Select(static entry => (entry.MediatR!, entry.Relevox, entry.Behavior)),
            ];
            return new Types(handlers, pipeline, Resolve(compilation, MediatRMessages), relevoxMessages, Resolve(compilation, RelevoxHandlers));
        }

        public Classification? Classify(INamedTypeSymbol type)
        {
            if (type.TypeKind != TypeKind.Class || type.IsAbstract)
            {
                return null;
            }

            ImmutableArray<INamedTypeSymbol> interfaces = type.AllInterfaces;
            if (interfaces.Any(contract => _handlers.Contains(contract.OriginalDefinition)))
            {
                return new Classification(Kind.Handler, null);
            }

            foreach ((INamedTypeSymbol mediatr, INamedTypeSymbol? relevox, bool behavior) in _pipeline)
            {
                if (interfaces.Any(contract => SymbolEqualityComparer.Default.Equals(contract.OriginalDefinition, mediatr))
                    && (relevox is null || !interfaces.Any(contract => SymbolEqualityComparer.Default.Equals(contract.OriginalDefinition, relevox))))
                {
                    return new Classification(behavior ? Kind.Behavior : Kind.Processor, mediatr.Name);
                }
            }

            return null;
        }

        public bool HandlesOnRelevox(INamedTypeSymbol type) =>
            type.TypeKind == TypeKind.Class && type.AllInterfaces.Any(contract => _relevoxHandlers.Contains(contract.OriginalDefinition));

        public bool CarriesBoth(INamedTypeSymbol type) =>
            type.AllInterfaces.Any(contract => _mediatrMessages.Contains(contract.OriginalDefinition))
            && type.AllInterfaces.Any(contract => _relevoxMessages.Contains(contract.OriginalDefinition));

        /// <summary>The MediatR types of every referenced assembly that references MediatR, MediatR's own excluded.</summary>
        public IEnumerable<Classification> InReferences(Compilation compilation, CancellationToken cancellationToken)
        {
            foreach (IAssemblySymbol assembly in compilation.SourceModule.ReferencedAssemblySymbols)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (assembly.Name.StartsWith("MediatR", StringComparison.Ordinal)
                    || !assembly.Modules.Any(static module => module.ReferencedAssemblies.Any(static identity => identity.Name == "MediatR")))
                {
                    continue;
                }

                foreach (INamedTypeSymbol type in AllTypes(assembly.GlobalNamespace))
                {
                    if (Classify(type) is { } found)
                    {
                        yield return found;
                    }
                }
            }
        }

        private static IEnumerable<INamedTypeSymbol> AllTypes(INamespaceOrTypeSymbol container)
        {
            foreach (ISymbol member in container.GetMembers())
            {
                if (member is INamespaceOrTypeSymbol inner)
                {
                    if (inner is INamedTypeSymbol type)
                    {
                        yield return type;
                    }

                    foreach (INamedTypeSymbol nested in AllTypes(inner))
                    {
                        yield return nested;
                    }
                }
            }
        }

        private static HashSet<INamedTypeSymbol> Resolve(Compilation compilation, string[] names) =>
            new(
                names.Select(compilation.GetTypeByMetadataName).Where(static type => type is not null).Select(static type => type!),
                SymbolEqualityComparer.Default);
    }
}
