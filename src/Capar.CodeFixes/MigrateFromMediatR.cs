using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Capar.CodeFixes;

/// <summary>
/// Moves code from MediatR to Çapar where the compiler stops on it after the package swap.
/// </summary>
/// <remarks>
/// <para>
/// Once the MediatR package is gone, every <c>using MediatR;</c>, every name qualified with <c>MediatR.</c>
/// and every <c>AddMediatR</c> call is already a compiler error. Fixing those errors is the whole migration, so
/// this needs no analyzer of its own: it offers a fix on CS0246, CS0234 and CS1061 where they are about
/// MediatR, and "Fix all in solution" does the lot in one go.
/// </para>
/// <para>
/// Only what has a Çapar counterpart is rewritten. <c>using MediatR.Registration;</c> and the other namespaces
/// Çapar has no equivalent for are left alone, so the compiler keeps pointing at them.
/// </para>
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(MigrateFromMediatR))]
[Shared]
public sealed class MigrateFromMediatR : CodeFixProvider
{
    private const string Title = "Move from MediatR to Capar";

    // Namespaces under MediatR whose types Çapar has under the same name below Capar.
    private static readonly HashSet<string> Mapped = new(System.StringComparer.Ordinal) { "Pipeline", "NotificationPublishers", "Entities" };

    // Configuration members that only drive MediatR's assembly scan, its run-time closing of generic handlers or
    // its licence, none of which exists in Çapar.
    private static readonly HashSet<string> Dropped = new(System.StringComparer.Ordinal)
    {
        "RegisterServicesFromAssembly",
        "RegisterServicesFromAssemblies",
        "RegisterServicesFromAssemblyContaining",
        "AssembliesToRegister",
        "TypeEvaluator",
        "MediatorImplementationType",
        "RegisterGenericHandlers",
        "MaxGenericTypeParameters",
        "MaxGenericTypeRegistrations",
        "MaxTypesClosing",
        "RegistrationTimeout",
        "LicenseKey",
    };

    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ["CS0246", "CS0234", "CS1061"];

    /// <inheritdoc />
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <inheritdoc />
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        SyntaxNode? root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return;
        }

        foreach (Diagnostic diagnostic in context.Diagnostics)
        {
            SyntaxNode node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
            (SyntaxNode Target, SyntaxNode Replacement)? edit = diagnostic.Id == "CS1061"
                ? AddMediatRCall(node) is { } registration ? (registration, RewriteRegistration(registration)) : null
                : RenameMediatR(node);

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

    /// <summary>
    /// The name to replace and its Çapar spelling, or null when the name is not MediatR's or has no Çapar
    /// counterpart.
    /// </summary>
    private static (SyntaxNode Target, SyntaxNode Replacement)? RenameMediatR(SyntaxNode node)
    {
        if (node is IdentifierNameSyntax { Identifier.Text: "MediatRServiceConfiguration" } configuration)
        {
            return (configuration, configuration.WithIdentifier(SyntaxFactory.Identifier("CaparOptions").WithTriviaFrom(configuration.Identifier)));
        }

        // The diagnostic lands on the unresolved part: MediatR itself, or the whole qualified name.
        NameSyntax? name = node as NameSyntax;
        while (name?.Parent is QualifiedNameSyntax parent && parent.Left == name)
        {
            name = parent;
        }

        if (name is null)
        {
            return null;
        }

        string[] parts = name.ToString().Split('.');
        if (parts[0] != "MediatR" || (parts.Length > 1 && !Mapped.Contains(parts[1]) && IsNamespaceUse(name)))
        {
            return null;
        }

        IdentifierNameSyntax? root = name.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().FirstOrDefault(static identifier => identifier.Identifier.Text == "MediatR");
        return root is null
            ? null
            : (root, root.WithIdentifier(SyntaxFactory.Identifier("Capar").WithTriviaFrom(root.Identifier)));
    }

    // In a using directive the second part is a namespace, which must be one Çapar has. Elsewhere, as in
    // MediatR.IRequest<int>, it is a type in MediatR's root namespace, which Çapar has under the same name.
    private static bool IsNamespaceUse(NameSyntax name) => name.Parent is UsingDirectiveSyntax;

    private static InvocationExpressionSyntax? AddMediatRCall(SyntaxNode node) =>
        node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault(static invocation =>
            invocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "AddMediatR" });

    /// <summary>
    /// <c>AddMediatR(...)</c> as <c>AddCapar(...)</c>, keeping every configuration call that still means
    /// something and removing the ones that only drove the assembly scan.
    /// </summary>
    private static InvocationExpressionSyntax RewriteRegistration(InvocationExpressionSyntax invocation)
    {
        MemberAccessExpressionSyntax access = (MemberAccessExpressionSyntax)invocation.Expression;
        MemberAccessExpressionSyntax renamed = access.WithName(SyntaxFactory.IdentifierName("AddCapar").WithTriviaFrom(access.Name));

        ArgumentSyntax? kept = invocation.ArgumentList.Arguments.Count == 1
            && invocation.ArgumentList.Arguments[0].Expression is LambdaExpressionSyntax lambda
            && Trim(lambda) is { } trimmed
                ? invocation.ArgumentList.Arguments[0].WithExpression(trimmed)
                : null;

        // Assembly or type arguments, as in the old AddMediatR(typeof(Startup)), only told MediatR where to scan.
        ArgumentListSyntax arguments = kept is null
            ? SyntaxFactory.ArgumentList().WithTriviaFrom(invocation.ArgumentList)
            : invocation.ArgumentList.WithArguments(SyntaxFactory.SingletonSeparatedList(kept));

        return invocation.WithExpression(renamed).WithArgumentList(arguments);
    }

    /// <summary>The configuration lambda without the dropped members, or null when nothing is left in it.</summary>
    private static LambdaExpressionSyntax? Trim(LambdaExpressionSyntax lambda)
    {
        LambdaExpressionSyntax stripped = lambda.ReplaceNodes(
            lambda.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(static call =>
                call.Expression is MemberAccessExpressionSyntax member && Dropped.Contains(member.Name.Identifier.Text)),
            static (original, rewritten) => ((MemberAccessExpressionSyntax)rewritten.Expression).Expression.WithTriviaFrom(rewritten));

        if (stripped.Body is BlockSyntax block)
        {
            List<StatementSyntax> statements = [.. block.Statements.Where(static statement => !IsLeftover(statement))];
            return statements.Count == 0 ? null : stripped.WithBody(block.WithStatements(SyntaxFactory.List(statements)));
        }

        // An expression body that was only a dropped call is now just the parameter, which is not a statement.
        return stripped.Body is ExpressionSyntax body && IsLeftover(body) ? null : stripped;
    }

    private static bool IsLeftover(StatementSyntax statement) =>
        statement is ExpressionStatementSyntax { Expression: var expression } && IsLeftover(expression);

    private static bool IsLeftover(ExpressionSyntax expression) => expression switch
    {
        // What a dropped call in a chain leaves behind: the receiver on its own, as in `options;`.
        IdentifierNameSyntax => true,
        AssignmentExpressionSyntax { Left: MemberAccessExpressionSyntax member } => Dropped.Contains(member.Name.Identifier.Text),
        _ => false,
    };
}
