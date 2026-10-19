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
using Relevox.Migration;

namespace Relevox.CodeFixes;

/// <summary>
/// Moves code from MediatR to Relevox: the fix for RVX0007.
/// </summary>
/// <remarks>
/// <para>
/// RVX0007 points at three kinds of leftover, and this rewrites each: a name rooted at <c>MediatR</c> becomes
/// one rooted at <c>Relevox</c>, <c>MediatRServiceConfiguration</c> becomes <c>RelevoxOptions</c>, and an
/// <c>AddMediatR</c> call becomes <c>AddRelevox</c> without the settings that only drove MediatR's assembly
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
            SyntaxNode node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
            diagnostic.Properties.TryGetValue("Kind", out string? kind);

            (SyntaxNode Target, SyntaxNode Replacement)? edit = (kind, node) switch
            {
                ("Name", IdentifierNameSyntax name) when name.FirstAncestorOrSelf<UsingDirectiveSyntax>() is { Parent: { } block } => (block, WithUsingsMoved(block)),
                ("Name", IdentifierNameSyntax name) => (name, Renamed(name, "Relevox")),
                ("Configuration", IdentifierNameSyntax name) => (name, Renamed(name, "RelevoxOptions")),
                ("Strategy", IdentifierNameSyntax name) => (name, Qualified(name)),
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

    // RequestExceptionActionProcessorStrategy, which code that configured MediatR found through
    // Microsoft.Extensions.DependencyInjection, written with the namespace Relevox declares it in.
    private static SyntaxNode Qualified(IdentifierNameSyntax name)
    {
        const string Full = "Relevox.RequestExceptionActionProcessorStrategy";
        SyntaxNode qualified = SyntaxFacts.IsInTypeOnlyContext(name) ? SyntaxFactory.ParseTypeName(Full) : SyntaxFactory.ParseExpression(Full);
        return qualified.WithTriviaFrom(name);
    }

    /// <summary>
    /// The file or namespace with every movable MediatR using in it renamed, and kept in order if it was in order.
    /// </summary>
    /// <remarks>
    /// Renamed in place, <c>using MediatR;</c> becomes <c>using Relevox;</c> where MediatR sorted, before
    /// Microsoft, and a solution that checks its formatting fails after the migration. The whole block is redone
    /// for each using in it, so every fix for the block makes the same change and "Fix all" merges them as one.
    /// </remarks>
    private static SyntaxNode WithUsingsMoved(SyntaxNode block)
    {
        SyntaxList<UsingDirectiveSyntax> usings = block switch
        {
            CompilationUnitSyntax unit => unit.Usings,
            BaseNamespaceDeclarationSyntax space => space.Usings,
            _ => default,
        };

        UsingDirectiveSyntax[] renamed =
        [
            .. usings.Select(static directive => directive.ReplaceNodes(
                directive.NamespaceOrType.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
                    .Where(static name => name.Identifier.ValueText == "MediatR" && MediatRUsageAnalyzer.IsMovable(name)),
                static (_, name) => Renamed(name, "Relevox"))),
        ];

        // A file that already had `using Relevox;` next to `using MediatR;` would end up with it twice, which is a
        // compiler warning, and an error under warnings as errors. The renamed copy goes. A comment at the end of
        // its line moves to the copy that stays; a copy with a comment or directive above it, or one whose comment
        // has nowhere to go, stays rather than lose it.
        Dictionary<string, int> kept = new(System.StringComparer.Ordinal);
        for (int index = 0; index < renamed.Length; index++)
        {
            if (renamed[index].IsEquivalentTo(usings[index]) && !kept.ContainsKey(Identity(renamed[index])))
            {
                kept[Identity(renamed[index])] = index;
            }
        }

        bool[] dropped = new bool[renamed.Length];
        for (int index = 0; index < renamed.Length; index++)
        {
            if (renamed[index].IsEquivalentTo(usings[index]))
            {
                continue;
            }

            string identity = Identity(renamed[index]);
            if (!kept.TryGetValue(identity, out int keeper))
            {
                kept[identity] = index;
                continue;
            }

            if (!IsPlain(renamed[index].GetLeadingTrivia()))
            {
                continue;
            }

            if (!IsPlain(renamed[index].GetTrailingTrivia()))
            {
                if (!IsPlain(renamed[keeper].GetTrailingTrivia()))
                {
                    continue;
                }

                renamed[keeper] = renamed[keeper].WithTrailingTrivia(renamed[index].GetTrailingTrivia());
            }

            dropped[index] = true;
        }

        usings = SyntaxFactory.List(usings.Where((_, index) => !dropped[index]));
        renamed = [.. renamed.Where((_, index) => !dropped[index])];

        // Each position keeps its own trivia, so a file header, a blank line or a missing final newline stays where
        // it was.
        // A comment at the end of a using's line is about that using, and would stay at its position if it moved.
        UsingDirectiveSyntax[] moved = IsInOrder(usings) && renamed.All(static directive => IsPlain(directive.GetTrailingTrivia()))
            ? [
                .. renamed.OrderBy(static directive => directive, UsingOrder.Instance).Select((directive, index) => directive
                    .WithLeadingTrivia(renamed[index].GetLeadingTrivia())
                    .WithTrailingTrivia(renamed[index].GetTrailingTrivia())),
            ]
            : renamed;

        return block switch
        {
            CompilationUnitSyntax unit => unit.WithUsings(SyntaxFactory.List(moved)),
            BaseNamespaceDeclarationSyntax space => space.WithUsings(SyntaxFactory.List(moved)),
            _ => block,
        };
    }

    private static bool IsPlain(SyntaxTriviaList trivia) =>
        trivia.All(static item => item.IsKind(SyntaxKind.WhitespaceTrivia) || item.IsKind(SyntaxKind.EndOfLineTrivia));

    // What makes two using directives the same one: kind, alias and the name, ignoring trivia.
    private static string Identity(UsingDirectiveSyntax directive) =>
        $"{directive.GlobalKeyword.RawKind}|{directive.StaticKeyword.RawKind}|{directive.Alias?.Name.Identifier.ValueText}|{directive.NamespaceOrType.WithoutTrivia()}";

    private static bool IsInOrder(SyntaxList<UsingDirectiveSyntax> usings)
    {
        // A directive such as #if belongs to the usings around it, so moving them would change what compiles: a
        // block with one is left in its order. A comment between usings is left alone for the same reason; only
        // the first using's, a file header, stays at the top.
        if (usings.Any(static directive => directive.GetLeadingTrivia().Any(static trivia => trivia.IsDirective))
            || usings.Skip(1).Any(static directive => directive.GetLeadingTrivia().Any(static trivia => !trivia.IsKind(SyntaxKind.WhitespaceTrivia) && !trivia.IsKind(SyntaxKind.EndOfLineTrivia))))
        {
            return false;
        }

        for (int index = 1; index < usings.Count; index++)
        {
            if (UsingOrder.Instance.Compare(usings[index - 1], usings[index]) > 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The order the IDE and <c>dotnet format</c> sort usings in by default: global ones first, then plain, static and
    /// alias directives, System namespaces first, each part of a name compared ignoring case.
    /// </summary>
    private sealed class UsingOrder : IComparer<UsingDirectiveSyntax>
    {
        public static UsingOrder Instance { get; } = new();

        public int Compare(UsingDirectiveSyntax? x, UsingDirectiveSyntax? y)
        {
            if (x is null || y is null)
            {
                return x is null ? (y is null ? 0 : -1) : 1;
            }

            int order = Group(x).CompareTo(Group(y));
            if (order != 0)
            {
                return order;
            }

            string[] left = Key(x).Split('.');
            string[] right = Key(y).Split('.');
            order = IsSystem(right).CompareTo(IsSystem(left));
            for (int part = 0; order == 0 && part < left.Length && part < right.Length; part++)
            {
                order = System.StringComparer.OrdinalIgnoreCase.Compare(left[part], right[part]);
            }

            return order != 0 ? order : left.Length.CompareTo(right.Length);
        }

        private static int Group(UsingDirectiveSyntax directive) =>
            (directive.GlobalKeyword.RawKind == 0 ? 3 : 0) + (directive.Alias is not null ? 2 : directive.StaticKeyword.RawKind != 0 ? 1 : 0);

        private static string Key(UsingDirectiveSyntax directive) =>
            directive.Alias is { } alias ? alias.Name.Identifier.ValueText : directive.NamespaceOrType.ToString();

        private static bool IsSystem(string[] parts) => parts[0] == "System";
    }

    private static InvocationExpressionSyntax? Registration(SimpleNameSyntax method) =>
        method.Parent switch
        {
            MemberAccessExpressionSyntax { Parent: InvocationExpressionSyntax call } access when access.Name == method && call.Expression == access => call,
            InvocationExpressionSyntax call when call.Expression == method => call,
            _ => null,
        };

    /// <summary>
    /// <c>AddMediatR(...)</c> as <c>AddRelevox(...)</c>, or null when the configuration cannot be carried over.
    /// </summary>
    private static InvocationExpressionSyntax? Rewrite(InvocationExpressionSyntax call, SimpleNameSyntax method, SemanticModel model, CancellationToken cancellationToken)
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
