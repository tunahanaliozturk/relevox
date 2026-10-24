using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Relevox.Migration;

/// <summary>
/// Points at handlers that send another request, and at sends that lead back to the request being handled.
/// </summary>
/// <remarks>
/// <para>
/// A send inside a handler runs a second pipeline inside the first: behaviours, processors and telemetry all run
/// again, and a loop between two handlers is found only when the stack overflows. Both are visible at build time,
/// because the handlers and the sends are in the code.
/// </para>
/// <para>
/// Shipped in <c>Relevox.Contracts</c> with the migration analyzer, because handlers mostly live in libraries that
/// reference only the contracts. Every rule here is Info by default (ADR 0018).
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HandlerSendAnalyzer : DiagnosticAnalyzer
{
    /// <summary>A handler that sends a request or creates a stream.</summary>
    public static readonly DiagnosticDescriptor NestedSend = new(
        id: "RVX0008",
        title: "Handler sends another request",
        messageFormat: "'{0}' sends '{1}' from inside a handler, so a second pipeline runs inside this one",
        category: "Relevox",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        helpLinkUri: "https://github.com/tunahanaliozturk/relevox/blob/main/docs/diagnostics.md#rvx0008",
        description: "A request sent from a handler goes through its own pipeline, behaviours and processors included, inside the pipeline of the request being handled. Calling the code the other handler would run, as a service, is usually what was meant.");

    /// <summary>A send that leads, through handlers in this project, back to the request being handled.</summary>
    public static readonly DiagnosticDescriptor SendCycle = new(
        id: "RVX0009",
        title: "Handlers send each other in a cycle",
        messageFormat: "The handler of '{0}' sends '{1}', which leads back to '{0}': {2}",
        category: "Relevox",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        customTags: [WellKnownDiagnosticTags.CompilationEnd],
        helpLinkUri: "https://github.com/tunahanaliozturk/relevox/blob/main/docs/diagnostics.md#rvx0009",
        description: "Unless something along the way stops it, a request whose handler ends up sending the same request again recurses until the stack overflows. Only handlers in this project are followed.");

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [NestedSend, SendCycle];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static start =>
        {
            if (start.Compilation.GetTypeByMetadataName("Relevox.IBaseRequest") is null)
            {
                return;
            }

            ConcurrentDictionary<INamedTypeSymbol, ImmutableArray<ITypeSymbol>> handled = new(SymbolEqualityComparer.Default);
            ConcurrentBag<Edge> edges = [];
            start.RegisterOperationAction(operation => AnalyzeInvocation(operation, handled, edges), OperationKind.Invocation);
            start.RegisterCompilationEndAction(end => ReportCycles(end, edges));
        });
    }

    private static void AnalyzeInvocation(
        OperationAnalysisContext context,
        ConcurrentDictionary<INamedTypeSymbol, ImmutableArray<ITypeSymbol>> handled,
        ConcurrentBag<Edge> edges)
    {
        IInvocationOperation invocation = (IInvocationOperation)context.Operation;
        IMethodSymbol method = invocation.TargetMethod;
        if (method.Name is not ("Send" or "CreateStream") || !IsMediator(method.ContainingType))
        {
            return;
        }

        // A lambda or local function belongs to the type it is written in.
        INamedTypeSymbol? handler = context.ContainingSymbol as INamedTypeSymbol ?? context.ContainingSymbol.ContainingType;
        if (handler is null)
        {
            return;
        }

        ImmutableArray<ITypeSymbol> requests = handled.GetOrAdd(handler, HandledRequests);
        if (requests.IsDefault)
        {
            return;
        }

        ITypeSymbol? message = MessageType(invocation);
        Location location = NameLocation(invocation);
        context.ReportDiagnostic(Diagnostic.Create(NestedSend, location, handler.Name, message?.ToDisplayString() ?? "a request"));

        // Only a known, closed request type can be followed to its handler.
        if (message is null or ITypeParameterSymbol || requests.IsEmpty)
        {
            return;
        }

        // A send inside Handle(TRequest, ...), written plainly or as an explicit interface implementation, is from
        // that request; anywhere else in a class that handles several, it is counted from each of them.
        ITypeSymbol? from = context.ContainingSymbol is IMethodSymbol { Parameters.Length: > 0 } handle
            && (handle.Name == "Handle" || handle.ExplicitInterfaceImplementations.Any(static implemented => implemented.Name == "Handle"))
            ? requests.FirstOrDefault(request => SymbolEqualityComparer.Default.Equals(request, handle.Parameters[0].Type))
            : null;
        foreach (ITypeSymbol request in from is null ? requests : [from])
        {
            edges.Add(new Edge(request, message, location));
        }
    }

    /// <summary>
    /// What the type handles: request and stream request types, empty for a notification handler, default for a
    /// type that is not a handler at all.
    /// </summary>
    private static ImmutableArray<ITypeSymbol> HandledRequests(INamedTypeSymbol type)
    {
        ImmutableArray<ITypeSymbol>.Builder? requests = null;
        bool handler = false;
        foreach (INamedTypeSymbol contract in type.AllInterfaces)
        {
            if (!IsRelevox(contract))
            {
                continue;
            }

            switch (contract.Name)
            {
                case "IRequestHandler" or "IStreamRequestHandler":
                    handler = true;
                    if (contract.TypeArguments[0] is not ITypeParameterSymbol)
                    {
                        (requests ??= ImmutableArray.CreateBuilder<ITypeSymbol>()).Add(contract.TypeArguments[0]);
                    }

                    break;
                case "INotificationHandler":
                    handler = true;
                    break;
                default:
                    break;
            }
        }

        return !handler ? default : requests?.ToImmutable() ?? [];
    }

    private static void ReportCycles(CompilationAnalysisContext context, ConcurrentBag<Edge> edges)
    {
        if (edges.IsEmpty)
        {
            return;
        }

        Dictionary<ITypeSymbol, List<Edge>> graph = new(SymbolEqualityComparer.Default);
        foreach (Edge edge in edges)
        {
            if (!graph.TryGetValue(edge.From, out List<Edge>? outgoing))
            {
                graph[edge.From] = outgoing = [];
            }

            outgoing.Add(edge);
        }

        foreach (Edge edge in edges.OrderBy(static edge => edge.Location.SourceTree?.FilePath, System.StringComparer.Ordinal).ThenBy(static edge => edge.Location.SourceSpan.Start))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            List<ITypeSymbol>? back = PathBack(graph, edge.To, edge.From);
            if (back is not null)
            {
                string path = string.Join(" -> ", new[] { edge.From }.Concat(back).Select(static type => type.Name));
                context.ReportDiagnostic(Diagnostic.Create(SendCycle, edge.Location, edge.From.ToDisplayString(), edge.To.ToDisplayString(), path));
            }
        }
    }

    /// <summary>The shortest chain of sends from <paramref name="start"/> to <paramref name="target"/>, both included, or null.</summary>
    private static List<ITypeSymbol>? PathBack(Dictionary<ITypeSymbol, List<Edge>> graph, ITypeSymbol start, ITypeSymbol target)
    {
        Dictionary<ITypeSymbol, ITypeSymbol?> previous = new(SymbolEqualityComparer.Default) { [start] = null };
        Queue<ITypeSymbol> queue = new([start]);
        while (queue.Count > 0)
        {
            ITypeSymbol current = queue.Dequeue();
            if (SymbolEqualityComparer.Default.Equals(current, target))
            {
                List<ITypeSymbol> path = [];
                for (ITypeSymbol? step = current; step is not null; step = previous[step])
                {
                    path.Add(step);
                }

                path.Reverse();
                return path;
            }

            if (graph.TryGetValue(current, out List<Edge>? outgoing))
            {
                foreach (Edge next in outgoing)
                {
                    if (!previous.ContainsKey(next.To))
                    {
                        previous[next.To] = current;
                        queue.Enqueue(next.To);
                    }
                }
            }
        }

        return null;
    }

    /// <summary>The static type of the message argument, before any conversion to the parameter's type.</summary>
    private static ITypeSymbol? MessageType(IInvocationOperation invocation)
    {
        IArgumentOperation? argument = invocation.Arguments.FirstOrDefault(static argument => argument.Parameter?.Ordinal == 0);
        IOperation? value = argument?.Value;
        while (value is IConversionOperation { IsImplicit: true } conversion)
        {
            value = conversion.Operand;
        }

        return value?.Type is { SpecialType: not SpecialType.System_Object } type and not IErrorTypeSymbol ? type : null;
    }

    private static Location NameLocation(IInvocationOperation invocation) => invocation.Syntax switch
    {
        InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } => access.Name.GetLocation(),
        InvocationExpressionSyntax { Expression: MemberBindingExpressionSyntax binding } => binding.Name.GetLocation(),
        _ => invocation.Syntax.GetLocation(),
    };

    private static bool IsMediator(INamedTypeSymbol? type) =>
        type is not null && (IsSender(type) || type.AllInterfaces.Any(IsSender));

    private static bool IsSender(INamedTypeSymbol type) => IsRelevox(type) && type.Name is "ISender" or "IMediator";

    private static bool IsRelevox(INamedTypeSymbol type) =>
        type.ContainingNamespace is { Name: "Relevox", ContainingNamespace.IsGlobalNamespace: true };

    private sealed class Edge(ITypeSymbol from, ITypeSymbol to, Location location)
    {
        public ITypeSymbol From { get; } = from;

        public ITypeSymbol To { get; } = to;

        public Location Location { get; } = location;
    }
}
