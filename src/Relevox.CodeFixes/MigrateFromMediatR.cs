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
using Microsoft.CodeAnalysis.Formatting;
using Relevox.Migration;

namespace Relevox.CodeFixes;

/// <summary>
/// Moves code from MediatR to Relevox: the fix for RVX0007.
/// </summary>
/// <remarks>
/// <para>
/// RVX0007 points at three kinds of leftover, and this rewrites each: a name rooted at <c>MediatR</c>, in code or
/// in a documentation <c>cref</c>, becomes one rooted at <c>Relevox</c>, <c>MediatRServiceConfiguration</c> becomes
/// <c>RelevoxOptions</c>, and an <c>AddMediatR</c> call becomes <c>AddRelevox</c> without the settings that only
/// drove MediatR's assembly scan, its licence check or its run-time closing of generic handlers. Because every
/// kind shares the one id, "Fix all in solution" does the whole migration in one go.
/// </para>
/// <para>
/// An options object passed to <c>AddMediatR</c> becomes the lambda <c>AddRelevox</c> takes when it is made in
/// the call, or in a local filled in by plain statements and used nowhere else
/// (<see cref="MediatRUsageAnalyzer.OptionsObject"/>). Where the configuration cannot be carried over without
/// guessing, as when the object is passed to a method or built in another one, no fix is offered and the compiler
/// error stays. Removing code is done so that comments and preprocessor directives around it survive.
/// </para>
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(MigrateFromMediatR))]
[Shared]
public sealed class MigrateFromMediatR : CodeFixProvider
{
    private const string Title = "Move from MediatR to Relevox";

    // Configuration members that only drive MediatR's assembly scan, its run-time closing of generic handlers or
    // its licence, none of which exists in Relevox.
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
    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ["RVX0007"];

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
            // Inside trivia too: a cref lives in a documentation comment.
            SyntaxNode node = root.FindNode(diagnostic.Location.SourceSpan, findInsideTrivia: true, getInnermostNodeForTie: true);
            diagnostic.Properties.TryGetValue("Kind", out string? kind);

            (SyntaxNode Target, SyntaxNode Replacement)? edit = (kind, node) switch
            {
                ("Cref", _) when node.FirstAncestorOrSelf<XmlCrefAttributeSyntax>() is { } attribute => (attribute.Cref, MediatRUsageAnalyzer.Moved(attribute.Cref)),
                ("Name", IdentifierNameSyntax name) when name.FirstAncestorOrSelf<UsingDirectiveSyntax>() is { Parent: { } block } => (block, UsingsMover.Moved(block, static candidate => candidate.Identifier.ValueText == "MediatR" && MediatRUsageAnalyzer.IsMovable(candidate))),
                ("Name" or "Configuration" or "Strategy", IdentifierNameSyntax name) => (name, Renamed(name, kind)),
                ("Registration", SimpleNameSyntax method) when Registration(method) is { } call
                    && Rewrite(call, method, model, context.CancellationToken) is { } rewritten => rewritten,
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

    private static SyntaxNode Renamed(IdentifierNameSyntax name, string kind) =>
        kind switch
        {
            "Name" => UsingsMover.Renamed(name, "Relevox"),
            "Configuration" => UsingsMover.Renamed(name, "RelevoxOptions"),
            _ => Qualified(name),
        };

    // RequestExceptionActionProcessorStrategy, which code that configured MediatR found through
    // Microsoft.Extensions.DependencyInjection, written with the namespace Relevox declares it in.
    private static SyntaxNode Qualified(IdentifierNameSyntax name)
    {
        const string Full = "Relevox.RequestExceptionActionProcessorStrategy";
        SyntaxNode qualified = SyntaxFacts.IsInTypeOnlyContext(name) ? SyntaxFactory.ParseTypeName(Full) : SyntaxFactory.ParseExpression(Full);
        return qualified.WithTriviaFrom(name);
    }

    private static InvocationExpressionSyntax? Registration(SimpleNameSyntax method) =>
        method.Parent switch
        {
            MemberAccessExpressionSyntax { Parent: InvocationExpressionSyntax call } access when access.Name == method && call.Expression == access => call,
            InvocationExpressionSyntax call when call.Expression == method => call,
            _ => null,
        };

    /// <summary>
    /// <c>AddMediatR(...)</c> as <c>AddRelevox(...)</c>, or null when the configuration cannot be carried over. The
    /// edit is the call, or the block holding it when a local options object goes.
    /// </summary>
    private static (SyntaxNode Target, SyntaxNode Replacement)? Rewrite(InvocationExpressionSyntax call, SimpleNameSyntax method, SemanticModel model, CancellationToken cancellationToken)
    {
        IdentifierNameSyntax addRelevox = SyntaxFactory.IdentifierName("AddRelevox").WithTriviaFrom(method);
        ExpressionSyntax expression = call.Expression switch
        {
            // The static form, MediatRServiceCollectionExtensions.AddMediatR(services, ...), keeps its first
            // argument and calls the generated class instead.
            MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "MediatRServiceCollectionExtensions" } owner } access =>
                access.WithExpression(SyntaxFactory.IdentifierName("RelevoxServiceCollectionExtensions").WithTriviaFrom(owner)).WithName(addRelevox),
            MemberAccessExpressionSyntax access => access.WithName(addRelevox),
            _ => addRelevox,
        };

        bool staticForm = call.Expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "MediatRServiceCollectionExtensions" } };
        List<ArgumentSyntax> kept = [];
        ImmutableArray<SyntaxNode> options = [];
        for (int index = 0; index < call.ArgumentList.Arguments.Count; index++)
        {
            ArgumentSyntax argument = call.ArgumentList.Arguments[index];
            if (staticForm && index == 0)
            {
                kept.Add(argument);
                continue;
            }

            if (options.IsEmpty && MediatRUsageAnalyzer.OptionsObject(argument, model, cancellationToken) is { IsEmpty: false } parts)
            {
                options = parts;
                if (Trim(Lambda(parts, model, cancellationToken)) is { } lambda)
                {
                    kept.Add(argument.WithNameColon(null).WithExpression(lambda));
                }

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

        InvocationExpressionSyntax rewritten = call
            .WithExpression(expression)
            .WithArgumentList(call.ArgumentList.WithArguments(SyntaxFactory.SeparatedList(kept)));
        if (options.IsEmpty || options[0] is not LocalDeclarationStatementSyntax { Parent: BlockSyntax block } declaration)
        {
            return (call, rewritten);
        }

        // The local and the statements that filled it in go: the lambda has them now. Comments and directives above
        // the declaration stay where they were; those on a moved statement went with it.
        BlockSyntax edited = block.TrackNodes([call, .. options]);
        edited = edited.ReplaceNode(edited.GetCurrentNode(call)!, rewritten);
        for (int index = options.Length - 1; index >= 0; index--)
        {
            edited = Remove(edited, (StatementSyntax)edited.GetCurrentNode(options[index])!, keepComments: options[index] == declaration);
        }

        return (block, edited);
    }

    /// <summary>
    /// The lambda an options object becomes: its initializer's assignments, the calls chained after its creation and
    /// the statements that filled it in, each made on the lambda's parameter, which takes the local's name.
    /// </summary>
    private static SimpleLambdaExpressionSyntax Lambda(ImmutableArray<SyntaxNode> parts, SemanticModel model, CancellationToken cancellationToken)
    {
        // The MediatR names inside are this fix's to move: the analyzer leaves them alone, so its edits cannot
        // overlap this one.
        SyntaxNode[] moved =
        [
            .. parts.Select(part => part.ReplaceNodes(
                part.DescendantNodes().OfType<IdentifierNameSyntax>().Where(name => MediatRUsageAnalyzer.KindOf(name, model, cancellationToken) is not null),
                (name, _) => Renamed(name, MediatRUsageAnalyzer.KindOf(name, model, cancellationToken)!))),
        ];

        (string parameter, ExpressionSyntax value) = moved[0] is LocalDeclarationStatementSyntax declaration
            ? (declaration.Declaration.Variables[0].Identifier.ValueText, declaration.Declaration.Variables[0].Initializer!.Value)
            : ("options", (ExpressionSyntax)moved[0]);

        ExpressionSyntax creation = value;
        while (creation is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Expression: { } receiver } })
        {
            creation = receiver;
        }

        IdentifierNameSyntax self = SyntaxFactory.IdentifierName(parameter);
        List<StatementSyntax> statements =
        [
            .. ((BaseObjectCreationExpressionSyntax)creation).Initializer?.Expressions.Cast<AssignmentExpressionSyntax>().Select(assignment =>
                Statement(assignment.WithLeft(SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, self, (SimpleNameSyntax)assignment.Left)))) ?? [],
        ];
        if (value != creation)
        {
            statements.Add(Statement(value.ReplaceNode(creation, self)));
        }

        statements.AddRange(moved.Skip(1).Cast<StatementSyntax>());
        SimpleLambdaExpressionSyntax lambda = SyntaxFactory.SimpleLambdaExpression(SyntaxFactory.Parameter(SyntaxFactory.Identifier(parameter)));
        return statements.Count == 1 && statements[0] is ExpressionStatementSyntax { Expression: var only } && moved.Length == 1
            ? lambda.WithExpressionBody(only)
            : lambda.WithBlock(SyntaxFactory.Block(statements)).WithAdditionalAnnotations(Formatter.Annotation);

        // A statement on a line of its own, laid out by the formatter. Elastic trivia leaves the layout to it, and
        // the line break keeps it from treating the block as one written on a single line, which it would keep.
        static ExpressionStatementSyntax Statement(ExpressionSyntax expression) =>
            SyntaxFactory.ExpressionStatement(expression.WithoutTrivia()).WithLeadingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed);
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
        // before the call, cannot be passed to AddRelevox, which takes a delegate.
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
    /// A statement moved elsewhere has taken its comments with it, so they are not kept.
    /// </summary>
    private static T Remove<T>(T root, StatementSyntax statement, bool keepComments = true)
        where T : SyntaxNode
    {
        SyntaxTriviaList leading = statement.GetLeadingTrivia();
        List<SyntaxTrivia> kept = [];
        if (keepComments && leading.Any(static trivia => !trivia.IsKind(SyntaxKind.WhitespaceTrivia) && !trivia.IsKind(SyntaxKind.EndOfLineTrivia)))
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
        T marked = root.ReplaceToken(following, following.WithAdditionalAnnotations(next));
        StatementSyntax target = marked.DescendantNodes().OfType<StatementSyntax>().First(candidate => candidate.SpanStart == statement.SpanStart && candidate.IsKind(statement.Kind()));
        T removed = marked.RemoveNode(target, SyntaxRemoveOptions.KeepNoTrivia)!;
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
