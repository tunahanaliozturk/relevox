using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Capar.CodeFixes;

/// <summary>
/// Moves code from MediatR to Çapar: the fix for CPR0007.
/// </summary>
/// <remarks>
/// <para>
/// CPR0007 points at three kinds of leftover, and this rewrites each: a name rooted at <c>MediatR</c> becomes
/// one rooted at <c>Capar</c>, <c>MediatRServiceConfiguration</c> becomes <c>CaparOptions</c>, and an
/// <c>AddMediatR</c> call becomes <c>AddCapar</c> without the settings that only drove MediatR's assembly
/// scan, its licence check or its run-time closing of generic handlers. Because every kind shares the one id,
/// "Fix all in solution" does the whole migration in one go.
/// </para>
/// <para>
/// Where the configuration cannot be carried over without guessing, as when it is built as an object and
/// passed in, no fix is offered and the compiler error stays. Removing code is done so that comments and
/// preprocessor directives around it survive.
/// </para>
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(MigrateFromMediatR))]
[Shared]
public sealed class MigrateFromMediatR : CodeFixProvider
{
    private const string Title = "Move from MediatR to Capar";

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
    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ["CPR0007"];

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
            diagnostic.Properties.TryGetValue("Kind", out string? kind);

            (SyntaxNode Target, SyntaxNode Replacement)? edit = (kind, node) switch
            {
                ("Name", IdentifierNameSyntax name) => (name, Renamed(name, "Capar")),
                ("Configuration", IdentifierNameSyntax name) => (name, Renamed(name, "CaparOptions")),
                ("Registration", SimpleNameSyntax method) when Registration(method) is { } call
                    && Rewrite(call, method, model, context.CancellationToken) is { } rewritten => (call, rewritten),
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

    private static IdentifierNameSyntax Renamed(IdentifierNameSyntax name, string to) =>
        name.WithIdentifier(SyntaxFactory.Identifier(to).WithTriviaFrom(name.Identifier));

    private static InvocationExpressionSyntax? Registration(SimpleNameSyntax method) =>
        method.Parent switch
        {
            MemberAccessExpressionSyntax { Parent: InvocationExpressionSyntax call } access when access.Name == method && call.Expression == access => call,
            InvocationExpressionSyntax call when call.Expression == method => call,
            _ => null,
        };

    /// <summary>
    /// <c>AddMediatR(...)</c> as <c>AddCapar(...)</c>, or null when the configuration cannot be carried over.
    /// </summary>
    private static InvocationExpressionSyntax? Rewrite(InvocationExpressionSyntax call, SimpleNameSyntax method, SemanticModel model, CancellationToken cancellationToken)
    {
        IdentifierNameSyntax addCapar = SyntaxFactory.IdentifierName("AddCapar").WithTriviaFrom(method);
        ExpressionSyntax expression = call.Expression switch
        {
            // The static form, MediatRServiceCollectionExtensions.AddMediatR(services, ...), keeps its first
            // argument and calls the generated class instead.
            MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "MediatRServiceCollectionExtensions" } owner } access =>
                access.WithExpression(SyntaxFactory.IdentifierName("CaparServiceCollectionExtensions").WithTriviaFrom(owner)).WithName(addCapar),
            MemberAccessExpressionSyntax access => access.WithName(addCapar),
            _ => addCapar,
        };

        bool staticForm = call.Expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "MediatRServiceCollectionExtensions" } };
        List<ArgumentSyntax> kept = [];
        for (int index = 0; index < call.ArgumentList.Arguments.Count; index++)
        {
            ArgumentSyntax argument = call.ArgumentList.Arguments[index];
            if (staticForm && index == 0)
            {
                kept.Add(argument);
                continue;
            }

            switch (Classify(argument.Expression, model, cancellationToken))
            {
                case Argument.Scan:
                    break;
                case Argument.Configuration:
                    if (Trim((AnonymousFunctionExpressionSyntax)argument.Expression) is { } trimmed)
                    {
                        // The generated parameter is called configure, so a named argument would not bind.
                        kept.Add(argument.WithNameColon(null).WithExpression(trimmed));
                    }

                    break;
                case Argument.Delegate:
                    kept.Add(argument.WithNameColon(null));
                    break;
                default:
                    return null;
            }
        }

        if (kept.Count > (staticForm ? 2 : 1))
        {
            return null;
        }

        return call
            .WithExpression(expression)
            .WithArgumentList(call.ArgumentList.WithArguments(SyntaxFactory.SeparatedList(kept)));
    }

    private enum Argument
    {
        Unknown,

        // Assemblies or types to scan, as in AddMediatR(typeof(Startup)) or the 11.x second argument.
        Scan,

        // A lambda or anonymous method configuring the options, trimmed and kept.
        Configuration,

        // A method group or delegate, kept as it is.
        Delegate,
    }

    private static Argument Classify(ExpressionSyntax expression, SemanticModel model, CancellationToken cancellationToken)
    {
        if (expression is AnonymousFunctionExpressionSyntax)
        {
            return Argument.Configuration;
        }

        ITypeSymbol? type = model.GetTypeInfo(expression, cancellationToken).Type;
        if (type is IArrayTypeSymbol array)
        {
            type = array.ElementType;
        }

        if (type?.ToDisplayString() is "System.Type" or "System.Reflection.Assembly")
        {
            return Argument.Scan;
        }

        // A method group has no type of its own; a delegate does. An options object, built and filled in
        // before the call, cannot be passed to AddCapar, which takes a delegate.
        return type is null && model.GetSymbolInfo(expression, cancellationToken).CandidateSymbols.Concat([model.GetSymbolInfo(expression, cancellationToken).Symbol]).Any(static symbol => symbol is IMethodSymbol)
            || type is INamedTypeSymbol { TypeKind: TypeKind.Delegate }
                ? Argument.Delegate
                : Argument.Unknown;
    }

    /// <summary>
    /// The configuration function without the dropped members, or null when nothing worth keeping is left.
    /// </summary>
    private static AnonymousFunctionExpressionSyntax? Trim(AnonymousFunctionExpressionSyntax function)
    {
        // A dropped call in a chain hands its receiver on: options.RegisterServicesFromAssembly(a).AddOpenBehavior(b)
        // becomes options.AddOpenBehavior(b).
        AnonymousFunctionExpressionSyntax trimmed = function.ReplaceNodes(
            function.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(static call =>
                call.Expression is MemberAccessExpressionSyntax member && Dropped.Contains(member.Name.Identifier.ValueText)),
            static (_, rewritten) => ((MemberAccessExpressionSyntax)rewritten.Expression).Expression.WithTriviaFrom(rewritten));

        if (trimmed.ExpressionBody is { } body && IsLeftover(body))
        {
            return null;
        }

        // Statements left as a bare receiver, or assigning a dropped member, are removed one at a time, each in a
        // way that keeps the code around it compiling and its comments and directives in place.
        while (trimmed.DescendantNodes().FirstOrDefault(IsLeftoverStatement) is { } leftover)
        {
            trimmed = leftover switch
            {
                StatementSyntax { Parent: BlockSyntax } statement => Remove(trimmed, statement),
                StatementSyntax statement => trimmed.ReplaceNode(statement, SyntaxFactory.Block().WithTriviaFrom(statement)),
                ExpressionSyntax { Parent: LambdaExpressionSyntax lambda } => trimmed.ReplaceNode(lambda, lambda.WithExpressionBody(null).WithBlock(SyntaxFactory.Block())),
                _ => trimmed,
            };
        }

        return trimmed.Block is { Statements.Count: 0 } block && !HasCommentOrDirective(block) ? null : trimmed;
    }

    /// <summary>
    /// Removes a statement from its block, keeping the comments and directives above it on the next line. Its
    /// indentation goes with it: carried over, it would indent the next line twice. When it opened the block,
    /// the blank lines that separated it from the next statement go too, so the block does not start with one.
    /// </summary>
    private static AnonymousFunctionExpressionSyntax Remove(AnonymousFunctionExpressionSyntax function, StatementSyntax statement)
    {
        SyntaxTriviaList leading = statement.GetLeadingTrivia();
        List<SyntaxTrivia> kept = [];
        if (leading.Any(static trivia => !trivia.IsKind(SyntaxKind.WhitespaceTrivia) && !trivia.IsKind(SyntaxKind.EndOfLineTrivia)))
        {
            int last = leading.Count - 1;
            while (last >= 0 && leading[last].IsKind(SyntaxKind.WhitespaceTrivia))
            {
                last--;
            }

            kept.AddRange(leading.Take(last + 1));
        }

        SyntaxAnnotation next = new();
        SyntaxToken following = statement.GetLastToken().GetNextToken();
        AnonymousFunctionExpressionSyntax marked = function.ReplaceToken(following, following.WithAdditionalAnnotations(next));
        StatementSyntax target = marked.DescendantNodes().OfType<StatementSyntax>().First(candidate => candidate.SpanStart == statement.SpanStart && candidate.IsKind(statement.Kind()));
        AnonymousFunctionExpressionSyntax removed = marked.RemoveNode(target, SyntaxRemoveOptions.KeepNoTrivia)!;
        SyntaxToken landing = removed.GetAnnotatedTokens(next).Single();
        SyntaxTriviaList rest = landing.LeadingTrivia;
        if (kept.Count == 0 && statement.Parent is BlockSyntax block && block.Statements[0] == statement)
        {
            // Everything up to the last line break before the next statement's own trivia begins.
            int start = 0;
            for (int index = 0; index < rest.Count && (rest[index].IsKind(SyntaxKind.WhitespaceTrivia) || rest[index].IsKind(SyntaxKind.EndOfLineTrivia)); index++)
            {
                if (rest[index].IsKind(SyntaxKind.EndOfLineTrivia))
                {
                    start = index + 1;
                }
            }

            rest = SyntaxFactory.TriviaList(rest.Skip(start));
        }

        return removed.ReplaceToken(landing, landing.WithLeadingTrivia(SyntaxFactory.TriviaList(kept).AddRange(rest)));
    }

    // A statement or a nested lambda's expression body that a dropped member left with nothing to do.
    private static bool IsLeftoverStatement(SyntaxNode node) => node switch
    {
        ExpressionStatementSyntax { Expression: var expression } => IsLeftover(expression),
        ExpressionSyntax { Parent: LambdaExpressionSyntax lambda } body => lambda.ExpressionBody == body && IsLeftover(body),
        _ => false,
    };

    private static bool IsLeftover(ExpressionSyntax expression) => expression switch
    {
        // What a dropped call leaves behind: the receiver on its own, as in `options;`.
        IdentifierNameSyntax or ThisExpressionSyntax => true,
        AssignmentExpressionSyntax { Left: MemberAccessExpressionSyntax member } => Dropped.Contains(member.Name.Identifier.ValueText),
        _ => false,
    };

    private static bool HasCommentOrDirective(BlockSyntax block) =>
        block.DescendantTrivia().Any(static trivia => !trivia.IsKind(SyntaxKind.WhitespaceTrivia) && !trivia.IsKind(SyntaxKind.EndOfLineTrivia));
}
