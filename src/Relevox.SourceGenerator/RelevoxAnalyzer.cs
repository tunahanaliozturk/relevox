using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace Relevox.SourceGenerator;

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
public sealed class RelevoxAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
    [
        Diagnostics.NoHandler,
        Diagnostics.HandlerOnlyInGeneratedCode,
        Diagnostics.AmbiguousHandler,
        Diagnostics.Inaccessible,
        Diagnostics.GenericHandler,
        Diagnostics.OwnMediator,
        Diagnostics.ForeignRegistration,
    ];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        // Generated code is left out here and, by the same rules, in the generator (GeneratedCode.cs). The two
        // have to agree on what is a handler and whether AddRelevox is called, and the generator cannot see what
        // other generators write, so the analyzer must not either.
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static start =>
        {
            // A project that cannot see the contracts has nothing to say.
            if (start.Compilation.GetTypeByMetadataName("Relevox.IBaseRequest") is null)
            {
                return;
            }

            // Where this project's own AddRelevox is, worked out the way the generator works it out, so the warning
            // can name the call that reaches it.
            string assemblyName = start.Compilation.AssemblyName ?? "Relevox.Generated";
            string ownRegistration = "global::"
                + Planner.RegistrationNamespaceFor(assemblyName, Discovery.SeesAnotherRegistration(start.Compilation))
                + ".RelevoxServiceCollectionExtensions";

            start.RegisterOperationAction(
                operation =>
                {
                    IMethodSymbol method = ((IInvocationOperation)operation.Operation).TargetMethod;
                    if (method.Name == "AddRelevox"
                        && method.ContainingType is { Name: "RelevoxServiceCollectionExtensions" } registration
                        && !SymbolEqualityComparer.Default.Equals(registration.ContainingAssembly, operation.Compilation.Assembly)
                        && Discovery.IsGeneratedByRelevox(registration))
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
            int callsAddRelevox = 0;
            start.RegisterSyntaxNodeAction(
                call =>
                {
                    if (RelevoxGenerator.IsAddRelevoxCall(call.Node))
                    {
                        Interlocked.Exchange(ref callsAddRelevox, 1);
                    }
                },
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.InvocationExpression);
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
                EmitPlan plan = Planner.Build(
                    end.Compilation.AssemblyName ?? "Relevox.Generated",
                    Discovery.Merge([.. parts, references, closings]),
                    mediatorCoversReferences: Discovery.MediatorCoversReferences(end.Compilation, end.CancellationToken),
                    ownOpenProcessors: parts.Any(static part => part.Open.Length > 0),
                    callsAddRelevox: Volatile.Read(ref callsAddRelevox) == 1);

                Dictionary<string, INamedTypeSymbol>? leftOut = null;
                foreach (DiagnosticEntry entry in plan.Diagnostics)
                {
                    if (entry.Rule == Diagnostics.NoHandler.Id
                        && (leftOut ??= GeneratedHandlers(end.Compilation, end.CancellationToken)).TryGetValue(entry.Arguments[0], out INamedTypeSymbol? handler))
                    {
                        // Every reason it is left out, since each alone keeps it out. The property tells the handler
                        // writer not to offer a second handler.
                        SyntaxTree file = handler.DeclaringSyntaxReferences.Select(static reference => reference.SyntaxTree)
                            .FirstOrDefault(tree => GeneratedCode.Contains(tree, end.Compilation, end.CancellationToken))
                            ?? handler.DeclaringSyntaxReferences[0].SyntaxTree;
                        List<string> remedies = [];
                        if (GeneratedCode.Contains(file, end.Compilation, end.CancellationToken))
                        {
                            remedies.Add("set generated_code = false for that file in .editorconfig");
                        }

                        if (GeneratedCode.MarkedBy(handler) is { } marked)
                        {
                            remedies.Add($"remove [GeneratedCode] from '{marked.ToDisplayString()}'");
                        }

                        end.ReportDiagnostic(Diagnostic.Create(
                            Diagnostics.HandlerOnlyInGeneratedCode,
                            InSource(entry.Location, end.Compilation),
                            ImmutableDictionary<string, string?>.Empty.Add(Diagnostics.GeneratedHandlerProperty, handler.ToDisplayString()),
                            entry.Arguments[0],
                            handler.ToDisplayString(),
                            Path.GetFileName(file.FilePath),
                            string.Join(" and ", remedies)));
                        continue;
                    }

                    end.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.Descriptor(entry.Rule),
                        InSource(entry.Location, end.Compilation),
                        [.. entry.Arguments]));
                }
            });
        });
    }

    /// <summary>
    /// Request and stream handlers that discovery left out because they are generated code, by the request they
    /// handle as RVX0001 names it. Only looked for when a request has no handler, and only for its message: whether
    /// a handler counts is decided by the same rules as in the generator, which never sees these.
    /// </summary>
    private static Dictionary<string, INamedTypeSymbol> GeneratedHandlers(Compilation compilation, CancellationToken cancellationToken)
    {
        Dictionary<string, INamedTypeSymbol> found = new(StringComparer.Ordinal);
        foreach (INamedTypeSymbol type in compilation.GetSymbolsWithName(static _ => true, SymbolFilter.Type, cancellationToken)
            .OfType<INamedTypeSymbol>()
            .Where(type => GeneratedCode.IsMarked(type) || type.DeclaringSyntaxReferences.Any(reference => GeneratedCode.Contains(reference.SyntaxTree, compilation, cancellationToken)))
            .OrderBy(static type => type.ToDisplayString(), StringComparer.Ordinal))
        {
            foreach (HandlerModel handler in Discovery.Classify(type, compilation, fromSource: true, cancellationToken).Handlers)
            {
                string request = Planner.Trim(handler.Message.Type);
                if (handler.Kind is HandlerKind.Request or HandlerKind.VoidRequest or HandlerKind.Stream && !found.ContainsKey(request))
                {
                    found.Add(request, type);
                }
            }
        }

        return found;
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
