using System.Collections.Immutable;
using System.Composition;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Relevox.Migration;

namespace Relevox.CodeFixes;

/// <summary>
/// Moves code from the Mediator library to Relevox: the fix for RVX0017.
/// </summary>
/// <remarks>
/// <para>
/// RVX0017 points at three kinds of leftover, and this rewrites each. A name rooted at <c>Mediator</c> becomes
/// one rooted at <c>Relevox</c>. A type in Mediator's shape, one whose base list names a command or a query or
/// whose <c>Handle</c> returns <c>ValueTask</c>, is rewritten as a whole: base list, return types, completed
/// returns, <c>next</c> calls and constraints. An <c>AddMediator</c> call becomes <c>AddRelevox</c> with the
/// lifetime written out. Because every kind shares the one id, "Fix all in solution" does the whole move in one
/// go. The mapping is ADR 0024's table.
/// </para>
/// <para>
/// Everything the fix does is syntactic, since once the Mediator package is gone nothing of its resolves. Where
/// the code cannot be carried over without guessing, as with an options object passed to <c>AddMediator</c> or a
/// pre-processor subclass, no fix is offered and the compiler error stays.
/// </para>
/// <para>
/// A diagnostic inside a type that is moved is fixed by rewriting that whole type, whatever its kind, so that the
/// fixes the batch fixer merges are the same change instead of overlapping ones.
/// </para>
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(MigrateFromMediator))]
[Shared]
public sealed class MigrateFromMediator : CodeFixProvider
{
    private const string Title = "Move from Mediator to Relevox";

    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ["RVX0017"];

    /// <inheritdoc />
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <inheritdoc />
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        SyntaxNode? root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        SemanticModel? model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null || model is null)
        {
            return;
        }

        foreach (Diagnostic diagnostic in context.Diagnostics)
        {
            SyntaxNode node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
            diagnostic.Properties.TryGetValue(MediatorUsageAnalyzer.KindProperty, out string? kind);

            (SyntaxNode Target, SyntaxNode Replacement)? edit = (kind, node) switch
            {
                (MediatorUsageAnalyzer.NameKind or MediatorUsageAnalyzer.TypeKind, _) when MediatorTypeRewriter.Outermost(node, model, context.CancellationToken) is { } type =>
                    (type, new MediatorTypeRewriter(model, context.CancellationToken).Rewrite(type)),
                (MediatorUsageAnalyzer.NameKind, IdentifierNameSyntax name) when name.FirstAncestorOrSelf<UsingDirectiveSyntax>() is { Parent: { } block } =>
                    (block, UsingsMover.Moved(block, candidate => candidate.Identifier.ValueText == "Mediator"
                        && MediatorSyntax.IsMovable(candidate)
                        && !MediatorSyntax.Resolves(model, candidate, context.CancellationToken))),
                (MediatorUsageAnalyzer.NameKind, IdentifierNameSyntax name) => (name, UsingsMover.Renamed(name, "Relevox")),
                (MediatorUsageAnalyzer.RegistrationKind, SimpleNameSyntax method) when Registration(method) is { } call
                    && MediatorRegistrationRewriter.Rewrite(call, method, model) is { } rewritten => (call, rewritten),
                _ => null,
            };

            if (edit is not { } change)
            {
                continue;
            }

            context.RegisterCodeFix(
                CodeAction.Create(
                    Title,
                    _ => Task.FromResult(context.Document.WithSyntaxRoot(root.ReplaceNode(change.Target, change.Replacement))),
                    equivalenceKey: Title),
                diagnostic);
        }
    }

    private static InvocationExpressionSyntax? Registration(SimpleNameSyntax method) =>
        method.Parent switch
        {
            MemberAccessExpressionSyntax { Parent: InvocationExpressionSyntax call } access when access.Name == method && call.Expression == access => call,
            InvocationExpressionSyntax call when call.Expression == method => call,
            _ => null,
        };
}
