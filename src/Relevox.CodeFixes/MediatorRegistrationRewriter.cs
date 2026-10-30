using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Relevox.CodeFixes;

/// <summary>
/// Rewrites <c>AddMediator(...)</c> as <c>AddRelevox(...)</c>, keeping what Mediator did, or says it cannot.
/// </summary>
/// <remarks>
/// <para>
/// Mediator's lifetime is <c>Singleton</c> unless set, and applies to the mediator, the handlers and the
/// behaviours alike, where Relevox's three settings are transient unless set. The rewrite writes the lifetime
/// out in each place rather than move to the transient default, which would be a change nobody asked for, made
/// silently. See ADR 0024.
/// </para>
/// <para>
/// The options lambda is read, not edited: each statement has to be an assignment to a member the ADR lists, or
/// there is no fix. The result is built afresh, so a lambda with a comment or a directive in it is left too,
/// rather than lose either without a word.
/// </para>
/// </remarks>
internal static class MediatorRegistrationRewriter
{
    private const string Owner = "MediatorDependencyInjectionExtensions";

    /// <summary><c>AddMediator(...)</c> as <c>AddRelevox(...)</c>, or null when the configuration cannot be carried over.</summary>
    /// <param name="call">The call.</param>
    /// <param name="method">The name of the method in it.</param>
    /// <param name="model">The semantic model of the file.</param>
    /// <returns>The rewritten call, or null.</returns>
    public static InvocationExpressionSyntax? Rewrite(InvocationExpressionSyntax call, SimpleNameSyntax method, SemanticModel model)
    {
        IdentifierNameSyntax addRelevox = SyntaxFactory.IdentifierName("AddRelevox").WithTriviaFrom(method);
        bool staticForm = call.Expression is MemberAccessExpressionSyntax { Expression: var owner } && EndsIn(owner, Owner);
        ExpressionSyntax expression = call.Expression switch
        {
            // The static form, MediatorDependencyInjectionExtensions.AddMediator(services, ...), keeps its first
            // argument and calls the generated class instead.
            MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax identifier } access when staticForm =>
                access.WithExpression(SyntaxFactory.IdentifierName("RelevoxServiceCollectionExtensions").WithTriviaFrom(identifier)).WithName(addRelevox),
            MemberAccessExpressionSyntax access when staticForm => access.WithName(addRelevox),
            MemberAccessExpressionSyntax access => access.WithName(addRelevox),
            _ => addRelevox,
        };

        SeparatedSyntaxList<ArgumentSyntax> arguments = call.ArgumentList.Arguments;
        int first = staticForm ? 1 : 0;
        if (arguments.Count < first || arguments.Count > first + 1)
        {
            return null;
        }

        ArgumentSyntax? configuration = arguments.Count > first ? arguments[first] : null;
        if (!Read(configuration?.Expression, out Settings settings, out string parameter))
        {
            return null;
        }

        string lifetime = settings.Lifetime ?? DefaultLifetime(model, call);
        ArgumentSyntax lambda = SyntaxFactory.Argument(Build(call, parameter, lifetime, settings));
        if (configuration is not null)
        {
            lambda = lambda.WithTriviaFrom(configuration);
        }

        ArgumentSyntax[] kept = [.. arguments.Take(first), lambda];
        return call
            .WithExpression(expression)
            .WithArgumentList(call.ArgumentList.WithArguments(SyntaxFactory.SeparatedList(kept)));
    }

    private static bool EndsIn(ExpressionSyntax expression, string name) => expression switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText == name,
        MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText == name,
        _ => false,
    };

    // ServiceLifetime.Singleton, written the way this file can name it: the extension method's namespace is
    // usually imported, since AddMediator is in it, but a static call or a file in another namespace may not.
    private static string DefaultLifetime(SemanticModel model, SyntaxNode at) =>
        model.LookupNamespacesAndTypes(at.SpanStart, name: "ServiceLifetime").Length > 0
            ? "ServiceLifetime.Singleton"
            : "Microsoft.Extensions.DependencyInjection.ServiceLifetime.Singleton";

    /// <summary>What a configuration lambda sets, in the terms of the move.</summary>
    private sealed class Settings
    {
        public string? Lifetime { get; set; }

        public List<TypeOfExpressionSyntax> Behaviors { get; set; } = [];

        public List<TypeOfExpressionSyntax> StreamBehaviors { get; set; } = [];

        public string? Publisher { get; set; }
    }

    // False when the lambda is not one the ADR carries over.
    private static bool Read(ExpressionSyntax? configuration, out Settings settings, out string parameter)
    {
        settings = new Settings();
        parameter = "options";
        if (configuration is null)
        {
            return true;
        }

        // A method group, an options object or null cannot be turned into settings.
        (string? Name, CSharpSyntaxNode? Body) parsed = configuration switch
        {
            SimpleLambdaExpressionSyntax lambda => (lambda.Parameter.Identifier.ValueText, lambda.Body),
            ParenthesizedLambdaExpressionSyntax { ParameterList.Parameters.Count: 1 } lambda => (lambda.ParameterList.Parameters[0].Identifier.ValueText, lambda.Body),
            _ => (null, null),
        };

        if (parsed.Name is not { } name || parsed.Body is not { } body || configuration.DescendantTrivia().Any(static trivia => !trivia.IsKind(SyntaxKind.WhitespaceTrivia) && !trivia.IsKind(SyntaxKind.EndOfLineTrivia)))
        {
            return false;
        }

        parameter = name;
        IEnumerable<ExpressionSyntax> statements;
        if (body is BlockSyntax block)
        {
            List<ExpressionSyntax> expressions = [];
            foreach (StatementSyntax statement in block.Statements)
            {
                if (statement is not ExpressionStatementSyntax expression)
                {
                    return false;
                }

                expressions.Add(expression.Expression);
            }

            statements = expressions;
        }
        else
        {
            statements = [(ExpressionSyntax)body];
        }

        foreach (ExpressionSyntax statement in statements)
        {
            if (statement is not AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.SimpleAssignmentExpression, Left: MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax target, Name: var member }, Right: var value }
                || target.Identifier.ValueText != name
                || !Apply(settings, member.Identifier.ValueText, value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Apply(Settings settings, string member, ExpressionSyntax value)
    {
        switch (member)
        {
            case "ServiceLifetime":
                // Written out several times, so only a name, which cannot do anything twice.
                if (value is not (IdentifierNameSyntax or MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax or MemberAccessExpressionSyntax }))
                {
                    return false;
                }

                settings.Lifetime = value.WithoutTrivia().ToString();
                return true;
            case "PipelineBehaviors" or "StreamPipelineBehaviors":
                if (Types(value) is not { } types)
                {
                    return false;
                }

                if (member == "PipelineBehaviors")
                {
                    settings.Behaviors = types;
                }
                else
                {
                    settings.StreamBehaviors = types;
                }

                return true;
            case "NotificationPublisherType":
                // Only Mediator's own two; a publisher of the project's has to be written against Relevox's.
                if (value is TypeOfExpressionSyntax { Type: { } type } && LastName(type) is "ForeachAwaitPublisher" or "TaskWhenAllPublisher")
                {
                    settings.Publisher = LastName(type);
                    return true;
                }

                return false;
            case "Namespace" or "GenerateTypesAsInternal" or "Assemblies":
                // Relevox finds handlers across the project and its references, and generates nothing named by the
                // application, so there is nothing to carry over.
                return true;
            default:
                return false;
        }
    }

    private static string? LastName(TypeSyntax type) => type switch
    {
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
        SimpleNameSyntax simple when type is not GenericNameSyntax => simple.Identifier.ValueText,
        _ => null,
    };

    // The typeof(...) expressions of a list written as an array or a collection expression, or null for anything else.
    private static List<TypeOfExpressionSyntax>? Types(ExpressionSyntax value)
    {
        IEnumerable<ExpressionSyntax>? elements = value switch
        {
            ArrayCreationExpressionSyntax { Initializer: { } initializer } => initializer.Expressions,
            ImplicitArrayCreationExpressionSyntax { Initializer: var initializer } => initializer.Expressions,
            CollectionExpressionSyntax collection when collection.Elements.All(static element => element is ExpressionElementSyntax) =>
                collection.Elements.Cast<ExpressionElementSyntax>().Select(static element => element.Expression),
            // new Type[0], which is how Mediator itself starts out.
            ArrayCreationExpressionSyntax { Type.RankSpecifiers: { Count: 1 } ranks } when ranks[0].Sizes is { Count: 1 } sizes && sizes[0] is LiteralExpressionSyntax { Token.ValueText: "0" } => [],
            _ => null,
        };

        return elements is not null && elements.All(static element => element is TypeOfExpressionSyntax)
            ? [.. elements.Cast<TypeOfExpressionSyntax>()]
            : null;
    }

    // An unbound generic such as typeof(Logging<,>), the open form Relevox's AddOpenBehavior wants.
    private static bool IsOpen(TypeOfExpressionSyntax type) =>
        (type.Type is QualifiedNameSyntax qualified ? qualified.Right : type.Type) is GenericNameSyntax { TypeArgumentList.Arguments: var arguments }
        && arguments.All(static argument => argument is OmittedTypeArgumentSyntax);

    private static LambdaExpressionSyntax Build(InvocationExpressionSyntax call, string parameter, string lifetime, Settings settings)
    {
        Layout text = new(call);
        List<string> lines =
        [
            $"{parameter}.Lifetime = {lifetime};",
            $"{parameter}.HandlerLifetime = {lifetime};",
        ];

        lines.AddRange(settings.Behaviors.Select(behavior => $"{parameter}.{(IsOpen(behavior) ? "AddOpenBehavior" : "AddBehavior")}({behavior.WithoutTrivia()}, {lifetime});"));
        lines.AddRange(settings.StreamBehaviors.Select(behavior => $"{parameter}.{(IsOpen(behavior) ? "AddOpenStreamBehavior" : "AddStreamBehavior")}({behavior.WithoutTrivia()}, {lifetime});"));
        if (settings.Publisher is { } publisher)
        {
            lines.Add($"{parameter}.NotificationPublisherType = typeof(Relevox.NotificationPublishers.{publisher});");
        }

        StringBuilder builder = new();
        builder.Append(parameter).Append(" =>").Append(text.NewLine).Append(text.Indent).Append('{').Append(text.NewLine);
        foreach (string line in lines)
        {
            builder.Append(text.Indent).Append(text.Step).Append(line).Append(text.NewLine);
        }

        builder.Append(text.Indent).Append('}');
        return (LambdaExpressionSyntax)SyntaxFactory.ParseExpression(builder.ToString());
    }

    /// <summary>The line ending and indentation of the file the call is in, so the new block looks like its neighbours.</summary>
    private readonly struct Layout
    {
        public Layout(SyntaxNode node)
        {
            Microsoft.CodeAnalysis.Text.SourceText source = node.SyntaxTree.GetText();
            NewLine = source.ToString().Contains("\r\n") ? "\r\n" : "\n";
            string line = source.Lines.GetLineFromPosition(node.SpanStart).ToString();
            Indent = string.Concat(line.TakeWhile(static character => character is ' ' or '\t'));
            Step = Indent.StartsWith("\t", System.StringComparison.Ordinal) ? "\t" : "    ";
        }

        public string NewLine { get; }

        public string Indent { get; }

        public string Step { get; }
    }
}
