using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Relevox.Migration;

namespace Relevox.CodeFixes;

/// <summary>
/// Rewrites a type declaration written for Mediator as one for Relevox, as a whole: its base list, its
/// <c>Handle</c> methods and its constraints in one pass, so a type is never half moved.
/// </summary>
/// <remarks>
/// <para>
/// It works on the original tree and asks the semantic model about original nodes only, because the Mediator
/// package is gone and the model can tell nothing but "unresolved" and "resolves to the project's own". What it
/// writes is built from the visited children, so a root renamed deeper in the declaration is kept.
/// </para>
/// <para>
/// A nested type that is not itself in Mediator's shape is left as it is, apart from the roots renamed
/// everywhere, which is what RVX0017's name diagnostic would have done to it.
/// </para>
/// </remarks>
internal sealed class MediatorTypeRewriter(SemanticModel model, CancellationToken cancellationToken) : CSharpSyntaxRewriter
{
    private bool active;

    /// <summary>The outermost type containing the node that is moved, or null when none is.</summary>
    /// <param name="node">Any node, usually a diagnostic's.</param>
    /// <param name="model">The semantic model of the file.</param>
    /// <param name="cancellationToken">The token to observe.</param>
    /// <returns>The declaration to hand to <see cref="Rewrite"/>.</returns>
    /// <remarks>
    /// Every diagnostic inside such a type, whichever kind, is fixed by rewriting that one declaration, so the
    /// fixes of a "Fix all" are the same change and merge as one instead of overlapping.
    /// </remarks>
    public static TypeDeclarationSyntax? Outermost(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken) =>
        node.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().LastOrDefault(type => MediatorSyntax.IsMigratable(type, model, cancellationToken));

    /// <summary>The declaration moved.</summary>
    /// <param name="declaration">The declaration returned by <see cref="Outermost"/>.</param>
    /// <returns>The rewritten declaration.</returns>
    public TypeDeclarationSyntax Rewrite(TypeDeclarationSyntax declaration) => (TypeDeclarationSyntax)Visit(declaration);

    /// <inheritdoc />
    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node) => Scoped(node, () => base.VisitClassDeclaration(node));

    /// <inheritdoc />
    public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node) => Scoped(node, () => base.VisitRecordDeclaration(node));

    /// <inheritdoc />
    public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node) => Scoped(node, () => base.VisitStructDeclaration(node));

    /// <inheritdoc />
    public override SyntaxNode? VisitInterfaceDeclaration(InterfaceDeclarationSyntax node) => Scoped(node, () => base.VisitInterfaceDeclaration(node));

    // Whether the type being visited is moved is decided per type, since a nested type can be in Mediator's shape
    // while its outer type is not, or the other way round.
    private SyntaxNode? Scoped(TypeDeclarationSyntax node, System.Func<SyntaxNode?> visit)
    {
        bool outer = active;
        active = MediatorSyntax.IsMigratable(node, model, cancellationToken);
        try
        {
            return visit();
        }
        finally
        {
            active = outer;
        }
    }

    /// <inheritdoc />
    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node) =>
        node.Identifier.ValueText == "Mediator" && MediatorSyntax.IsMovable(node) && !MediatorSyntax.Resolves(model, node, cancellationToken)
            ? UsingsMover.Renamed(node, "Relevox")
            : base.VisitIdentifierName(node);

    /// <inheritdoc />
    public override SyntaxNode? VisitSimpleBaseType(SimpleBaseTypeSyntax node) =>
        active && Mapped(node.Type) is { } mapped ? node.WithType(mapped) : base.VisitSimpleBaseType(node);

    /// <inheritdoc />
    public override SyntaxNode? VisitTypeParameterConstraintClause(TypeParameterConstraintClauseSyntax node)
    {
        if (!active)
        {
            return base.VisitTypeParameterConstraintClause(node);
        }

        // A constraint naming something in the table moves like a base type does. IMessage has no counterpart; what
        // it asked for, a message that is not null, is what Relevox's behaviours ask for with notnull.
        List<TypeParameterConstraintSyntax> kept = [];
        TypeParameterConstraintSyntax? message = null;
        int messageAt = -1;
        foreach (TypeParameterConstraintSyntax constraint in node.Constraints)
        {
            if (constraint is TypeConstraintSyntax { Type: var type } typeConstraint)
            {
                if (MediatorSyntax.Read(type, model, cancellationToken) is { Text: "IMessage", Arity: 0 })
                {
                    message = constraint;
                    messageAt = kept.Count;
                    continue;
                }

                kept.Add(Mapped(type) is { } mapped ? typeConstraint.WithType(mapped) : (TypeParameterConstraintSyntax)Visit(constraint));
            }
            else
            {
                kept.Add((TypeParameterConstraintSyntax)Visit(constraint));
            }
        }

        if (message is null)
        {
            return node.WithConstraints(SyntaxFactory.SeparatedList(kept));
        }

        if (kept.Count == 0)
        {
            kept.Add(SyntaxFactory.TypeConstraint(SyntaxFactory.IdentifierName("notnull")).WithTriviaFrom(message));
        }
        else if (messageAt == kept.Count)
        {
            // It was last: what ended the clause now ends the one before.
            kept[kept.Count - 1] = kept[kept.Count - 1].WithTrailingTrivia(message.GetTrailingTrivia());
        }
        else if (messageAt == 0)
        {
            kept[0] = kept[0].WithLeadingTrivia(message.GetLeadingTrivia());
        }

        return node.WithConstraints(SyntaxFactory.SeparatedList(kept));
    }

    /// <inheritdoc />
    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        if (!active || node.Identifier.ValueText != "Handle")
        {
            return base.VisitMethodDeclaration(node);
        }

        // Which parameters are Mediator's handler delegates, found on the original, before the types change.
        ParameterSyntax[] delegateParameters = [.. MediatorSyntax.DelegateParameters(node, model, cancellationToken)];
        int[] delegates = [.. delegateParameters.Select(parameter => node.ParameterList.Parameters.IndexOf(parameter))];

        bool valueTask = MediatorSyntax.ReturnsValueTask(node);
        if (!valueTask && delegates.Length == 0)
        {
            return base.VisitMethodDeclaration(node);
        }

        MethodDeclarationSyntax method = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;
        string message = method.ParameterList.Parameters.Count > 0 ? method.ParameterList.Parameters[0].Identifier.ValueText : string.Empty;
        List<(string Name, bool Stream)> nexts = [];
        foreach (int index in delegates)
        {
            ParameterSyntax parameter = method.ParameterList.Parameters[index];
            bool stream = Last(parameter.Type!)!.Identifier.ValueText == "StreamHandlerDelegate";
            nexts.Add((parameter.Identifier.ValueText, stream));
            method = method.ReplaceNode(parameter, parameter.WithType(RelevoxDelegate(parameter.Type!, stream)));
        }

        if (valueTask)
        {
            method = method.WithReturnType(Retyped(method.ReturnType));
            if (!method.Modifiers.Any(SyntaxKind.AsyncKeyword))
            {
                method = CompletedReturns(method, Last(method.ReturnType) is GenericNameSyntax task ? task.TypeArgumentList.Arguments[0] : null);
            }
        }

        return nexts.Count == 0 ? method : NextCalls(method, message, nexts);
    }

    // The simple name a type ends in: ValueTask in System.Threading.Tasks.ValueTask<int>.
    private static SimpleNameSyntax? Last(TypeSyntax type) => type switch
    {
        QualifiedNameSyntax qualified => qualified.Right,
        SimpleNameSyntax simple => simple,
        _ => null,
    };

    // The same type with its last name replaced, keeping the qualification and any type arguments.
    private static TypeSyntax WithLast(TypeSyntax type, SimpleNameSyntax replacement) =>
        type is QualifiedNameSyntax qualified ? qualified.WithRight(replacement).WithTriviaFrom(type) : replacement.WithTriviaFrom(type);

    private static TypeSyntax Retyped(TypeSyntax type)
    {
        SimpleNameSyntax last = Last(type)!;
        SimpleNameSyntax task = last is GenericNameSyntax generic
            ? generic.WithIdentifier(SyntaxFactory.Identifier("Task"))
            : SyntaxFactory.IdentifierName("Task");
        return WithLast(type, task.WithTriviaFrom(last));
    }

    // MessageHandlerDelegate<TMessage, TResponse> as RequestHandlerDelegate<TResponse>, and the stream one with
    // one type argument fewer likewise: the message is no longer passed on, so its type is not needed.
    private static TypeSyntax RelevoxDelegate(TypeSyntax type, bool stream)
    {
        GenericNameSyntax generic = (GenericNameSyntax)Last(type)!;
        GenericNameSyntax relevox = SyntaxFactory.GenericName(
            SyntaxFactory.Identifier(stream ? "StreamHandlerDelegate" : "RequestHandlerDelegate"),
            SyntaxFactory.TypeArgumentList(SyntaxFactory.SingletonSeparatedList(generic.TypeArgumentList.Arguments[1].WithLeadingTrivia(SyntaxFactory.TriviaList()))));
        TypeSyntax moved = type is QualifiedNameSyntax qualified ? qualified.WithLeft(RelevoxRoot(qualified.Left)).WithRight(relevox) : relevox;
        return moved.WithTriviaFrom(type);
    }

    // What a name qualified with Mediator is qualified with in Relevox.
    private static NameSyntax RelevoxRoot(NameSyntax qualifier) =>
        qualifier is IdentifierNameSyntax identifier
            ? UsingsMover.Renamed(identifier, "Relevox")
            : qualifier.ReplaceNodes(
                qualifier.DescendantNodes().OfType<IdentifierNameSyntax>().Where(static name => name.Identifier.ValueText == "Mediator"),
                static (_, name) => UsingsMover.Renamed(name, "Relevox"));

    // The ADR's table, for one type as written, or null when it is not a row of it.
    private TypeSyntax? Mapped(TypeSyntax type)
    {
        if (MediatorSyntax.Read(type, model, cancellationToken) is not { } name || !MediatorSyntax.IsMapped(name))
        {
            return null;
        }

        List<TypeSyntax> arguments = name.Simple is GenericNameSyntax generic
            ? [.. generic.TypeArgumentList.Arguments.Select(argument => (TypeSyntax)Visit(argument))]
            : [];
        NameSyntax? qualifier = name.Qualifier is { } mediator ? RelevoxRoot(mediator) : null;

        // A void request keeps Unit, spelled the way the name was: Mediator.ICommand becomes Relevox.IRequest<Relevox.Unit>.
        TypeSyntax Unit(bool spaced)
        {
            TypeSyntax unit = qualifier is null
                ? SyntaxFactory.IdentifierName("Unit")
                : SyntaxFactory.QualifiedName(qualifier, SyntaxFactory.IdentifierName("Unit"));
            return spaced ? unit.WithLeadingTrivia(SyntaxFactory.Space) : unit;
        }

        bool handler = name.Text.EndsWith("Handler", System.StringComparison.Ordinal);
        string target = name.Text.StartsWith("IStream", System.StringComparison.Ordinal)
            ? (handler ? "IStreamRequestHandler" : "IStreamRequest")
            : (handler ? "IRequestHandler" : "IRequest");
        if (name.Arity == 0)
        {
            arguments.Add(Unit(spaced: false));
        }
        else if (handler && name.Arity == 1)
        {
            arguments.Add(Unit(spaced: true));
        }

        GenericNameSyntax relevox = SyntaxFactory.GenericName(
            SyntaxFactory.Identifier(target),
            SyntaxFactory.TypeArgumentList(SyntaxFactory.SeparatedList(arguments)));
        TypeSyntax result = qualifier is null ? relevox : SyntaxFactory.QualifiedName(qualifier, relevox);
        return result.WithTriviaFrom(type);
    }

    // Task is what the handler returns now, so what it hands back already finished has to be a finished Task: the
    // completed forms Mediator code uses are rewritten; any other expression is left for the compiler, since only its
    // type tells whether it needs AsTask(), and that type went with the package.
    private static MethodDeclarationSyntax CompletedReturns(MethodDeclarationSyntax method, TypeSyntax? result)
    {
        if (method.ExpressionBody is { } body)
        {
            return method.WithExpressionBody(body.WithExpression(Completed(body.Expression, result)));
        }

        if (method.Body is null)
        {
            return method;
        }

        IEnumerable<ReturnStatementSyntax> returns = method.Body
            .DescendantNodes(static node => node is not (LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax))
            .OfType<ReturnStatementSyntax>()
            .Where(static statement => statement.Expression is not null);
        return method.ReplaceNodes(returns, (original, _) => original.WithExpression(Completed(original.Expression!, result)));
    }

    private static ExpressionSyntax Completed(ExpressionSyntax expression, TypeSyntax? result)
    {
        bool generic = result is not null;
        ExpressionSyntax? task = expression switch
        {
            // new ValueTask<T>(x) and new(x), where the method returns ValueTask<T>.
            ObjectCreationExpressionSyntax { Type: var type, ArgumentList.Arguments.Count: 1 } creation
                when Last(type) is GenericNameSyntax { Identifier.ValueText: "ValueTask" } =>
                    FromResult(creation.ArgumentList.Arguments[0].Expression),
            ImplicitObjectCreationExpressionSyntax { ArgumentList.Arguments.Count: 1 } creation when generic =>
                FromResult(creation.ArgumentList.Arguments[0].Expression),

            // ValueTask.FromResult(x), keeping a type argument if it was given.
            InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "ValueTask" } owner } access } invocation
                when access.Name.Identifier.ValueText == "FromResult" =>
                    invocation.WithExpression(access.WithExpression(UsingsMover.Renamed(owner, "Task"))),

            MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "ValueTask" } owner, Name.Identifier.ValueText: "CompletedTask" } access =>
                access.WithExpression(UsingsMover.Renamed(owner, "Task")),

            // Unit.ValueTask, which is a ValueTask<Unit> already completed.
            MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ValueTask", Expression: var unit } when EndsInUnit(unit) =>
                FromResult(SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, unit.WithoutTrivia(), SyntaxFactory.IdentifierName("Value"))),

            // default for a ValueTask that is not generic is the completed one.
            LiteralExpressionSyntax literal when !generic && literal.IsKind(SyntaxKind.DefaultLiteralExpression) => CompletedTask(),
            DefaultExpressionSyntax { Type: var type } when !generic && Last(type) is { Identifier.ValueText: "ValueTask" } and not GenericNameSyntax => CompletedTask(),

            // default for a ValueTask<T> is one completed with default(T). Left as it is, it would be a null Task,
            // which compiles without nullable analysis and throws where it is awaited.
            LiteralExpressionSyntax literal when generic && literal.IsKind(SyntaxKind.DefaultLiteralExpression) => DefaultResult(result!),
            DefaultExpressionSyntax { Type: var type } when generic && Last(type) is GenericNameSyntax { Identifier.ValueText: "ValueTask" } => DefaultResult(result!),
            _ => null,
        };

        return task?.WithTriviaFrom(expression) ?? expression;

        static ExpressionSyntax FromResult(ExpressionSyntax value) =>
            SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName("Task"), SyntaxFactory.IdentifierName("FromResult")),
                SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(value.WithoutTrivia()))));

        // Task.FromResult<T>(default!): the type argument keeps the result Task<T> rather than Task<T?>, and the
        // operator keeps nullable analysis from warning about a default it already accepted as ValueTask<T>'s.
        static ExpressionSyntax DefaultResult(TypeSyntax type) =>
            SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("Task"),
                    SyntaxFactory.GenericName(SyntaxFactory.Identifier("FromResult"), SyntaxFactory.TypeArgumentList(SyntaxFactory.SingletonSeparatedList(type.WithoutTrivia())))),
                SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(
                    SyntaxFactory.PostfixUnaryExpression(SyntaxKind.SuppressNullableWarningExpression, SyntaxFactory.LiteralExpression(SyntaxKind.DefaultLiteralExpression))))));

        static ExpressionSyntax CompletedTask() =>
            SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName("Task"), SyntaxFactory.IdentifierName("CompletedTask"));

        static bool EndsInUnit(ExpressionSyntax expression) => expression switch
        {
            IdentifierNameSyntax name => name.Identifier.ValueText == "Unit",
            MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText == "Unit",
            _ => false,
        };
    }

    // next(message, cancellationToken) as next(cancellationToken), and a stream's as next(). Only when the message
    // passed on is the one received: Relevox's pipeline carries one request, so a behaviour that hands a different
    // message down cannot be written there, and that call stays as it is for the compiler to point at.
    private static MethodDeclarationSyntax NextCalls(MethodDeclarationSyntax method, string message, List<(string Name, bool Stream)> nexts)
    {
        IEnumerable<InvocationExpressionSyntax> calls = method.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(call =>
            call.Expression is IdentifierNameSyntax callee
            && nexts.Any(next => next.Name == callee.Identifier.ValueText)
            && call.ArgumentList.Arguments.Count == 2
            && call.ArgumentList.Arguments[0].Expression is IdentifierNameSyntax first
            && first.Identifier.ValueText == message);

        return method.ReplaceNodes(calls, (call, _) =>
        {
            bool stream = nexts.First(next => next.Name == ((IdentifierNameSyntax)call.Expression).Identifier.ValueText).Stream;
            return call.WithArgumentList(stream
                ? call.ArgumentList.WithArguments(SyntaxFactory.SeparatedList<ArgumentSyntax>())
                : call.ArgumentList.WithArguments(SyntaxFactory.SingletonSeparatedList(call.ArgumentList.Arguments[1].WithLeadingTrivia(SyntaxFactory.TriviaList()))));
        });
    }
}
