using System.Collections.Immutable;
using System.Composition;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Relevox.CodeFixes;

/// <summary>
/// Writes the missing handler for a request: the fix for RVX0001.
/// </summary>
/// <remarks>
/// <para>
/// RVX0001 is the only rule that says a handler is missing. A notification with no handler is not reported, as
/// MediatR allows it, and the other rules are about handlers that exist. The handler goes in a new file next to
/// the request, with a body that throws <see cref="System.NotImplementedException"/>.
/// </para>
/// <para>
/// There is no fix-all, on purpose (ADR 0027). Applied to a whole project, or by <c>dotnet format</c>, it would
/// silence every RVX0001 with handlers that throw, turning a build warning back into the run-time failure the
/// rule exists to prevent.
/// </para>
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(WriteMissingHandler))]
[Shared]
public sealed class WriteMissingHandler : CodeFixProvider
{
    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ["RVX0001"];

    /// <inheritdoc />
    public override FixAllProvider? GetFixAllProvider() => null;

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
            if (root.FindNode(diagnostic.Location.SourceSpan).FirstAncestorOrSelf<BaseTypeDeclarationSyntax>() is { } declaration
                && model.GetDeclaredSymbol(declaration, context.CancellationToken) is { } request
                && MessageFiles.AddHandler(context.Document, request, model.Compilation) is { } action)
            {
                context.RegisterCodeFix(action, diagnostic);
            }
        }
    }
}
