using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace Capar.SourceGenerator;

/// <summary>
/// Reports what the generator found wrong, at the declarations it is about.
/// </summary>
/// <remarks>
/// <para>
/// The generator used to report these itself. A generator can only create a location from a file path and a
/// span, which Roslyn treats as a location outside the compilation: <c>#pragma warning disable</c> around the
/// declaration did not reach it, and the IDE listed the warning without underlining anything. An analyzer has
/// the compilation's own syntax trees, so the same diagnostics land in source, where suppressions, squiggles
/// and code fixes work.
/// </para>
/// <para>
/// It runs the same discovery and planning code as the generator, so the two cannot disagree about what is a
/// handler or which handler wins. The work is repeated, but analyzers run in the background, after the
/// generator has already produced the code the build needs.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CaparAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
    [
        Diagnostics.NoHandler,
        Diagnostics.AmbiguousHandler,
        Diagnostics.Inaccessible,
        Diagnostics.GenericHandler,
        Diagnostics.OwnMediator,
        Diagnostics.ForeignRegistration,
    ];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static start =>
        {
            // A project that cannot see the contracts has nothing to say.
            if (start.Compilation.GetTypeByMetadataName("Capar.IBaseRequest") is null)
            {
                return;
            }

            // Where this project's own AddCapar is, worked out the way the generator works it out, so the warning
            // can name the call that reaches it.
            string assemblyName = start.Compilation.AssemblyName ?? "Capar.Generated";
            string ownRegistration = "global::"
                + Planner.RegistrationNamespaceFor(assemblyName, Discovery.SeesAnotherRegistration(start.Compilation))
                + ".CaparServiceCollectionExtensions";

            start.RegisterOperationAction(
                operation =>
                {
                    IMethodSymbol method = ((IInvocationOperation)operation.Operation).TargetMethod;
                    if (method.Name == "AddCapar"
                        && method.ContainingType is { Name: "CaparServiceCollectionExtensions" } registration
                        && !SymbolEqualityComparer.Default.Equals(registration.ContainingAssembly, operation.Compilation.Assembly)
                        && Discovery.IsGeneratedByCapar(registration))
                    {
                        operation.ReportDiagnostic(Diagnostic.Create(
                            Diagnostics.ForeignRegistration,
                            operation.Operation.Syntax.GetLocation(),
                            registration.ContainingAssembly.Name,
                            ownRegistration));
                    }
                },
                OperationKind.Invocation);

            ConcurrentBag<DiscoveryModel> parts = [];
            start.RegisterSymbolAction(
                symbol =>
                {
                    DiscoveryModel model = Discovery.Classify((INamedTypeSymbol)symbol.Symbol, symbol.Compilation, fromSource: true, symbol.CancellationToken);
                    if (!model.IsEmpty)
                    {
                        parts.Add(model);
                    }
                },
                SymbolKind.NamedType);

            start.RegisterCompilationEndAction(end =>
            {
                DiscoveryModel references = Discovery.ScanReferences(end.Compilation, end.CancellationToken);
                DiscoveryModel closings = Discovery.CloseGenerics(end.Compilation, end.CancellationToken);
                EmitPlan plan = Planner.Build(end.Compilation.AssemblyName ?? "Capar.Generated", Discovery.Merge([.. parts, references, closings]));

                foreach (DiagnosticEntry entry in plan.Diagnostics)
                {
                    end.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.Descriptor(entry.Rule),
                        InSource(entry.Location, end.Compilation),
                        [.. entry.Arguments]));
                }
            });
        });
    }

    private static Location InSource(LocationModel? model, Compilation compilation)
    {
        if (model is null)
        {
            return Location.None;
        }

        SyntaxTree? tree = compilation.SyntaxTrees.FirstOrDefault(candidate => candidate.FilePath == model.FilePath);
        return tree is null
            ? Diagnostics.ToLocation(model)
            : Location.Create(tree, new TextSpan(model.Start, model.Length));
    }
}
