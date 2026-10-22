using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Relevox.SourceGenerator;

/// <summary>
/// Finds every handler this project can see and writes the mediator that calls them.
/// </summary>
/// <remarks>
/// <para>
/// Two sources feed one plan. Types declared in this project come through the syntax provider, which only
/// re-examines the declarations in a file that changed. Types in referenced projects come from the
/// compilation's references, filtered to assemblies that reference Relevox.Contracts. That second step runs
/// on every compilation, but it produces a value-equal model when nothing in the references changed, so
/// everything downstream of it stays cached.
/// </para>
/// <para>
/// Everything that crosses a stage boundary is strings and value-equal arrays. Models.cs says why that
/// matters.
/// </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class RelevoxGenerator : IIncrementalGenerator
{
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValueProvider<DiscoveryModel> fromSource = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => node is TypeDeclarationSyntax { BaseList: not null } declaration
                    && !declaration.Modifiers.Any(SyntaxKind.StaticKeyword),
                transform: static (syntax, cancellationToken) =>
                {
                    if (syntax.SemanticModel.GetDeclaredSymbol(syntax.Node, cancellationToken) is not INamedTypeSymbol symbol)
                    {
                        return Discovery.Empty;
                    }

                    // Generated code is checked for only once a type turns out to matter; most do not.
                    DiscoveryModel model = Discovery.Classify(symbol, syntax.SemanticModel.Compilation, fromSource: true, cancellationToken);
                    return model.IsEmpty
                        || (!GeneratedCode.Contains(syntax.Node.SyntaxTree, syntax.SemanticModel.Compilation, cancellationToken) && !GeneratedCode.IsMarked(symbol))
                        ? model
                        : Discovery.Empty;
                })
            .WithTrackingName(TrackingNames.Source)
            .Where(static model => !model.IsEmpty)
            .Collect()
            .Select(static (parts, _) => Discovery.Merge(parts))
            .WithTrackingName(TrackingNames.SourceMerged);

        IncrementalValueProvider<ReferenceModel> fromReferences = context.CompilationProvider
            .Select(static (compilation, cancellationToken) => new ReferenceModel(
                compilation.AssemblyName ?? "Relevox.Generated",
                Discovery.ScanReferences(compilation, cancellationToken),
                Discovery.SeesAnotherRegistration(compilation),
                Discovery.MediatorCoversReferences(compilation, cancellationToken)))
            .WithTrackingName(TrackingNames.References);

        // Generic handlers are closed over types found anywhere in the compilation, so this step looks at the
        // whole of it. Its output is value-equal from one edit to the next unless a generic handler or something
        // it could be closed over changed.
        IncrementalValueProvider<DiscoveryModel> closings = context.CompilationProvider
            .Select(static (compilation, cancellationToken) => Discovery.CloseGenerics(compilation, cancellationToken))
            .WithTrackingName(TrackingNames.Closings);

        // Found from syntax alone: the call cannot bind until this generator has written the method. Its options
        // lambda is read here too, for the message map; the plan only needs to know a call exists.
        IncrementalValueProvider<ImmutableArray<PipelineOptions>> addRelevoxCalls = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => IsAddRelevoxCall(node),
                transform: static (call, cancellationToken) => GeneratedCode.Contains(call.Node.SyntaxTree, call.SemanticModel.Compilation, cancellationToken)
                    ? null
                    : MessageMap.Read((InvocationExpressionSyntax)call.Node, call.SemanticModel, cancellationToken))
            .Where(static options => options is not null)
            .Select(static (options, _) => options!)
            .Collect();
        IncrementalValueProvider<bool> callsAddRelevox = addRelevoxCalls.Select(static (calls, _) => calls.Length > 0);

        IncrementalValueProvider<EmitPlan> plan = fromSource
            .Combine(fromReferences)
            .Combine(closings)
            .Combine(callsAddRelevox)
            .Select(static (pair, _) => Planner.Build(
                pair.Left.Left.Right.AssemblyName,
                Discovery.Merge([pair.Left.Left.Left, pair.Left.Left.Right.Discovery, pair.Left.Right]),
                pair.Left.Left.Right.SeesAnotherRegistration,
                pair.Left.Left.Right.MediatorCoversReferences,
                pair.Right,
                ownOpenProcessors: pair.Left.Left.Left.Open.Length > 0))
            .WithTrackingName(TrackingNames.Plan);

        // Diagnostics are reported by RelevoxAnalyzer, which has the syntax trees to put them in source.
        context.RegisterSourceOutput(plan, static (output, plan) =>
        {
            if (!plan.Emits)
            {
                return;
            }

            output.AddSource("RelevoxMediator.g.cs", SourceText.From(Emitter.Mediator(plan), Encoding.UTF8));
            output.AddSource("RelevoxServiceCollectionExtensions.g.cs", SourceText.From(Emitter.Registration(plan), Encoding.UTF8));
        });

        // Off by default. With it off this step yields null on every edit, so the output below stays cached and
        // the generated code is what it was before the map existed. With it on, the step binds names against the
        // compilation, so it runs on every edit, and its output only changes when the map does.
        IncrementalValueProvider<bool> mapRequested = context.AnalyzerConfigOptionsProvider
            .Select(static (options, _) => options.GlobalOptions.TryGetValue(MessageMap.Switch, out string? value)
                && string.Equals(value, "true", System.StringComparison.OrdinalIgnoreCase));

        IncrementalValueProvider<(string Namespace, string Json, string Markdown)?> map = plan
            .Combine(mapRequested)
            .Combine(addRelevoxCalls)
            .Combine(context.CompilationProvider)
            .Select(static (pair, _) => pair.Left.Left.Right && pair.Left.Left.Left.Emits
                ? MessageMap.Render(pair.Left.Left.Left, pair.Left.Right, pair.Right) is var (json, markdown) ? (pair.Left.Left.Left.Namespace, json, markdown) : null
                : ((string, string, string)?)null)
            .WithTrackingName(TrackingNames.MessageMap);

        context.RegisterSourceOutput(map, static (output, map) =>
        {
            if (map is { } value)
            {
                output.AddSource("RelevoxMessageMap.g.cs", SourceText.From(MessageMap.Source(value.Namespace, value.Json, value.Markdown), Encoding.UTF8));
            }
        });
    }

    /// <summary>
    /// A call to a method named <c>AddRelevox</c>: <c>services.AddRelevox()</c>, <c>services?.AddRelevox()</c> or
    /// <c>AddRelevox(services)</c>.
    /// </summary>
    internal static bool IsAddRelevoxCall(SyntaxNode node) =>
        node is InvocationExpressionSyntax
        {
            Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "AddRelevox" }
                or MemberBindingExpressionSyntax { Name.Identifier.ValueText: "AddRelevox" }
                or IdentifierNameSyntax { Identifier.ValueText: "AddRelevox" },
        };
}

/// <summary>Names of the pipeline steps, so tests can ask whether each one was served from cache.</summary>
internal static class TrackingNames
{
    public const string Source = "Source";
    public const string SourceMerged = "SourceMerged";
    public const string References = "References";
    public const string Closings = "Closings";
    public const string Plan = "Plan";
    public const string MessageMap = "MessageMap";
}
