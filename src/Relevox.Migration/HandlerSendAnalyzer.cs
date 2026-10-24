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

            ConcurrentDictionary<INamedTypeSymbol, ImmutableArray<Node>> handled = new(SymbolEqualityComparer.Default);
            ConcurrentBag<Edge> edges = [];
            start.RegisterOperationAction(operation => AnalyzeInvocation(operation, handled, edges), OperationKind.Invocation);
            start.RegisterCompilationEndAction(end => ReportCycles(end, edges));
        });
    }

    private static void AnalyzeInvocation(
        OperationAnalysisContext context,
        ConcurrentDictionary<INamedTypeSymbol, ImmutableArray<Node>> handled,
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

        ImmutableArray<Node> requests = handled.GetOrAdd(handler, HandledRequests);
        if (requests.IsDefault)
        {
            return;
        }

        ITypeSymbol? message = MessageType(invocation);
        Location location = NameLocation(invocation);
        context.ReportDiagnostic(Diagnostic.Create(NestedSend, location, handler.Name, message?.ToDisplayString() ?? "a request"));

        // Only a known, closed request type can be followed to its handler. An abstract handler is never the one
        // dispatched to, and which of its sends a concrete subclass keeps is not known here, so it adds no edges:
        // a cycle through one is missed rather than one reported that cannot happen.
        if (message is null or ITypeParameterSymbol || requests.IsEmpty || handler.IsAbstract)
        {
            return;
        }

        // A send inside a Handle that implements a handler interface, plainly or explicitly, is from the request that
        // Handle takes, sent or streamed as that interface says, or from none when it is a notification's. Anywhere
        // else, including a helper method that is also called Handle, it is counted from every request the class
        // handles.
        IEnumerable<Node> from = requests;
        if (context.ContainingSymbol is IMethodSymbol { Parameters.Length: > 0 } handle && Implemented(handler, handle) is { } contract)
        {
            Node own = new(handle.Parameters[0].Type, contract.Name == "IStreamRequestHandler");
            from = requests.Where(request => request.Equals(own));
        }

        Node to = new(message, method.Name == "CreateStream");
        foreach (Node request in from)
        {
            edges.Add(new Edge(request, to, location));
        }
    }

    /// <summary>The Relevox handler interface whose <c>Handle</c> the method implements for the type, or null.</summary>
    private static INamedTypeSymbol? Implemented(INamedTypeSymbol handler, IMethodSymbol method) =>
        method.Name != "Handle" && method.ExplicitInterfaceImplementations.Length == 0
            ? null
            : handler.AllInterfaces
                .Where(IsRelevox)
                .FirstOrDefault(contract => contract.GetMembers("Handle")
                    .Any(member => SymbolEqualityComparer.Default.Equals(handler.FindImplementationForInterfaceMember(member), method)));

    /// <summary>
    /// What the type handles: requests and stream requests, empty for a notification handler, default for a type
    /// that is not a handler at all.
    /// </summary>
    private static ImmutableArray<Node> HandledRequests(INamedTypeSymbol type)
    {
        ImmutableArray<Node>.Builder? requests = null;
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
                        (requests ??= ImmutableArray.CreateBuilder<Node>()).Add(new Node(contract.TypeArguments[0], contract.Name == "IStreamRequestHandler"));
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

        Dictionary<Node, List<Edge>> graph = [];
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
            List<Node>? back = PathBack(graph, edge.To, edge.From);
            if (back is not null)
            {
                string path = string.Join(" -> ", new[] { edge.From }.Concat(back));
                context.ReportDiagnostic(Diagnostic.Create(SendCycle, edge.Location, edge.From.Type.ToDisplayString(), edge.To.Type.ToDisplayString(), path));
            }
        }
    }

    /// <summary>The shortest chain of sends from <paramref name="start"/> to <paramref name="target"/>, both included, or null.</summary>
    private static List<Node>? PathBack(Dictionary<Node, List<Edge>> graph, Node start, Node target)
    {
        Dictionary<Node, Node?> previous = new() { [start] = null };
        Queue<Node> queue = new([start]);
        while (queue.Count > 0)
        {
            Node current = queue.Dequeue();
            if (current.Equals(target))
            {
                List<Node> path = [];
                for (Node? step = current; step is not null; step = previous[step])
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

    /// <summary>
    /// A request type and the way it is dispatched: a message can be both a request and a stream request, with a
    /// handler for each, and Send reaches only the one, CreateStream only the other.
    /// </summary>
    private sealed class Node(ITypeSymbol type, bool stream) : System.IEquatable<Node>
    {
        public ITypeSymbol Type { get; } = type;

        public bool Stream { get; } = stream;

        public bool Equals(Node? other) =>
            other is not null && other.Stream == Stream && SymbolEqualityComparer.Default.Equals(other.Type, Type);

        public override bool Equals(object? obj) => Equals(obj as Node);

        public override int GetHashCode() => (SymbolEqualityComparer.Default.GetHashCode(Type) * 2) + (Stream ? 1 : 0);

        public override string ToString() => Stream ? Type.Name + " (stream)" : Type.Name;
    }

    private sealed class Edge(Node from, Node to, Location location)
    {
        public Node From { get; } = from;

        public Node To { get; } = to;

        public Location Location { get; } = location;
    }
}
