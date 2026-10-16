using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Capar.SourceGenerator;

/// <summary>
/// Finds every handler this project can see and writes the mediator that calls them.
/// </summary>
/// <remarks>
/// <para>
/// Two sources feed one plan. Types declared in this project come through the syntax provider, which only
/// re-examines the declarations in a file that changed. Types in referenced projects come from the
/// compilation's references, filtered to assemblies that reference Capar.Contracts. That second step runs
/// on every compilation, but it produces a value-equal model when nothing in the references changed, so
/// everything downstream of it stays cached.
/// </para>
/// <para>
/// Everything that crosses a stage boundary is strings and value-equal arrays. Models.cs says why that
/// matters.
/// </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class CaparGenerator : IIncrementalGenerator
{
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValueProvider<DiscoveryModel> fromSource = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => node is TypeDeclarationSyntax { BaseList: not null } declaration
                    && !declaration.Modifiers.Any(SyntaxKind.StaticKeyword),
                transform: static (syntax, cancellationToken) =>
                    syntax.SemanticModel.GetDeclaredSymbol(syntax.Node, cancellationToken) is INamedTypeSymbol symbol
                        ? Discovery.Classify(symbol, syntax.SemanticModel.Compilation, fromSource: true, cancellationToken)
                        : Discovery.Empty)
            .WithTrackingName(TrackingNames.Source)
            .Where(static model => !model.IsEmpty)
            .Collect()
            .Select(static (parts, _) => Discovery.Merge(parts))
            .WithTrackingName(TrackingNames.SourceMerged);

        IncrementalValueProvider<ReferenceModel> fromReferences = context.CompilationProvider
            .Select(static (compilation, cancellationToken) => new ReferenceModel(
                compilation.AssemblyName ?? "Capar.Generated",
                Discovery.ScanReferences(compilation, cancellationToken)))
            .WithTrackingName(TrackingNames.References);

        IncrementalValueProvider<EmitPlan> plan = fromSource
            .Combine(fromReferences)
            .Select(static (pair, _) => Planner.Build(
                pair.Right.AssemblyName,
                Discovery.Merge([pair.Left, pair.Right.Discovery])))
            .WithTrackingName(TrackingNames.Plan);

        context.RegisterSourceOutput(plan, static (output, plan) =>
        {
            foreach (DiagnosticEntry entry in plan.Diagnostics)
            {
                output.ReportDiagnostic(Diagnostic.Create(
                    Descriptor(entry.Rule),
                    SourceGenerator.Diagnostics.ToLocation(entry.Location),
                    [.. entry.Arguments]));
            }

            output.AddSource("CaparMediator.g.cs", SourceText.From(Emitter.Mediator(plan), Encoding.UTF8));
            output.AddSource("CaparServiceCollectionExtensions.g.cs", SourceText.From(Emitter.Registration(plan), Encoding.UTF8));
        });
    }

    private static DiagnosticDescriptor Descriptor(string rule) => rule switch
    {
        "CPR0001" => SourceGenerator.Diagnostics.NoHandler,
        "CPR0002" => SourceGenerator.Diagnostics.AmbiguousHandler,
        "CPR0003" => SourceGenerator.Diagnostics.Inaccessible,
        "CPR0005" => SourceGenerator.Diagnostics.OwnMediator,
        _ => SourceGenerator.Diagnostics.GenericHandler,
    };
}

/// <summary>Names of the pipeline steps, so tests can ask whether each one was served from cache.</summary>
internal static class TrackingNames
{
    public const string Source = "Source";
    public const string SourceMerged = "SourceMerged";
    public const string References = "References";
    public const string Plan = "Plan";
}
