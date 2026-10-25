using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Relevox.Migration;

/// <summary>
/// Reports two behaviours that could run for the same request with the same <c>PipelineOrder</c>, which fall back to
/// registration order, the thing the attribute is there to remove (#98, ADR 0019).
/// </summary>
/// <remarks>
/// <para>
/// Pipeline and stream behaviours are checked apart. Two closed behaviours meet only when they are for the same
/// request type; an open behaviour may meet any other of its kind. Constraints on open behaviours are not
/// compared, so two whose constraints never hold for the same request are still reported.
/// </para>
/// <para>
/// Behaviours are taken from the project and the projects it references, and reported only where they are in
/// source: one in the project that clashes with one in a reference is reported here, two in a reference are
/// reported in that project.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PipelineOrderAnalyzer : DiagnosticAnalyzer
{
    private const string Attribute = "Relevox.PipelineOrderAttribute";

    /// <summary>Two behaviours with one order value.</summary>
    public static readonly DiagnosticDescriptor SameOrder = new(
        id: "RVX0013",
        title: "Two behaviours have the same PipelineOrder",
        messageFormat: "'{0}' has the same PipelineOrder, {1}, as '{2}', so the two run in the order they are registered",
        category: "Relevox",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        customTags: [WellKnownDiagnosticTags.CompilationEnd],
        helpLinkUri: "https://github.com/tunahanaliozturk/relevox/blob/main/docs/diagnostics.md#rvx0013",
        description: "Behaviours with the same PipelineOrder keep registration order. Give each its own number, or none if registration order is what you want.");

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [SameOrder];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static start =>
        {
            if (start.Compilation.GetTypeByMetadataName(Attribute) is null)
            {
                return;
            }

            ConcurrentBag<Behavior> found = [];
            start.RegisterSymbolAction(
                symbol =>
                {
                    if (Read((INamedTypeSymbol)symbol.Symbol) is { } behavior)
                    {
                        found.Add(behavior);
                    }
                },
                SymbolKind.NamedType);

            start.RegisterCompilationEndAction(end =>
            {
                Behavior[] own = [.. found.OrderBy(static behavior => behavior.Location.SourceTree?.FilePath, StringComparer.Ordinal).ThenBy(static behavior => behavior.Location.SourceSpan.Start)];
                if (own.Length == 0)
                {
                    return;
                }

                Behavior[] referenced =
                [
                    .. end.Compilation.SourceModule.ReferencedAssemblySymbols
                        .Where(static assembly => assembly.Modules.Any(static module => module.ReferencedAssemblySymbols.Any(static reference => reference.Name == "Relevox.Contracts")))
                        .SelectMany(Scan),
                ];

                // The first behaviour each one clashes with: references before the project, then in source order.
                for (int index = 0; index < own.Length; index++)
                {
                    Behavior behavior = own[index];
                    if (referenced.Concat(own.Take(index)).FirstOrDefault(earlier => earlier.Meets(behavior)) is { } first)
                    {
                        end.ReportDiagnostic(Diagnostic.Create(
                            SameOrder,
                            behavior.Location,
                            behavior.Type.ToDisplayString(),
                            behavior.Order,
                            first.Type.ToDisplayString()));
                    }
                }
            });
        });
    }

    /// <summary>
    /// The ordered behaviours a referenced assembly declares. Only looked for in a project that has an ordered
    /// behaviour of its own, and only in references that use Relevox.
    /// </summary>
    private static Behavior[] Scan(IAssemblySymbol assembly)
    {
        List<Behavior> behaviors = [];
        Stack<INamespaceOrTypeSymbol> pending = new([assembly.GlobalNamespace]);
        while (pending.Count > 0)
        {
            foreach (ISymbol member in pending.Pop().GetMembers())
            {
                if (member is INamespaceOrTypeSymbol container)
                {
                    pending.Push(container);
                }

                if (member is INamedTypeSymbol type && Read(type) is { } behavior)
                {
                    behaviors.Add(behavior);
                }
            }
        }

        return [.. behaviors];
    }

    /// <summary>A class with behaviour interfaces and the attribute, or null. The interfaces are the cheaper check.</summary>
    private static Behavior? Read(INamedTypeSymbol type)
    {
        if (type.TypeKind != TypeKind.Class || type.IsAbstract)
        {
            return null;
        }

        // Every closing counts: a class can be a behaviour for two requests, or for a request and a stream, and
        // is registered under each. A request type that still has a type parameter in it, such as Query<T>, can be
        // closed over many requests, so it is treated as open.
        (bool Stream, ITypeSymbol? Request)[] contracts =
        [
            .. type.AllInterfaces
                .Where(static candidate => candidate.Name is "IPipelineBehavior" or "IStreamPipelineBehavior"
                    && candidate.TypeArguments.Length == 2
                    && candidate.ContainingNamespace is { Name: "Relevox", ContainingNamespace.IsGlobalNamespace: true })
                .Select(static candidate => (candidate.Name == "IStreamPipelineBehavior", HasTypeParameters(candidate.TypeArguments[0]) ? null : candidate.TypeArguments[0])),
        ];
        if (contracts.Length == 0)
        {
            return null;
        }

        AttributeData? data = null;
        for (INamedTypeSymbol? current = type; current is not null && data is null; current = current.BaseType)
        {
            data = current.GetAttributes().FirstOrDefault(static candidate => candidate.AttributeClass?.ToDisplayString() == Attribute);
        }

        if (data is not { ConstructorArguments.Length: 1 } || data.ConstructorArguments[0].Value is not int order)
        {
            return null;
        }

        Location location = data.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? type.Locations.FirstOrDefault() ?? Location.None;
        return new Behavior(type, order, contracts, location);
    }

    private static bool HasTypeParameters(ITypeSymbol type) => type switch
    {
        ITypeParameterSymbol => true,
        IArrayTypeSymbol array => HasTypeParameters(array.ElementType),
        INamedTypeSymbol named => named.TypeArguments.Any(HasTypeParameters) || (named.ContainingType is { } outer && HasTypeParameters(outer)),
        _ => false,
    };

    private sealed class Behavior(INamedTypeSymbol type, int order, (bool Stream, ITypeSymbol? Request)[] contracts, Location location)
    {
        public INamedTypeSymbol Type { get; } = type;

        public int Order { get; } = order;

        /// <summary>
        /// Each behaviour interface it implements: pipeline or stream, and the request type when closed; null for an
        /// open one, which may meet any request.
        /// </summary>
        public (bool Stream, ITypeSymbol? Request)[] Contracts { get; } = contracts;

        public Location Location { get; } = location;

        /// <summary>Whether the two could run for one request with one order: same number, and a contract of the same kind and request.</summary>
        public bool Meets(Behavior other) =>
            other.Order == Order
            && !SymbolEqualityComparer.Default.Equals(other.Type, Type)
            && Contracts.Any(mine => other.Contracts.Any(theirs =>
                theirs.Stream == mine.Stream
                && (mine.Request is null || theirs.Request is null || SymbolEqualityComparer.Default.Equals(mine.Request, theirs.Request))));
    }
}
