using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Relevox.SourceGenerator;

/// <summary>A <c>Send</c>, <c>Publish</c> or <c>CreateStream</c> call whose message type is known at build time.</summary>
/// <param name="Message">The message's static type as the map writes it, such as <c>Shop.PlaceOrder</c>.</param>
/// <param name="File">The file the call is in, as the compiler has it.</param>
/// <param name="Line">The line of the method name, from 1.</param>
internal sealed record CallSite(string Message, string File, int Line);

/// <summary>Finds the places a project sends its messages from, for the message map (#46).</summary>
internal static class CallSites
{
    /// <summary>A call to a method with one of the mediator's sending names; whether it is one is decided by <see cref="Read"/>.</summary>
    public static bool IsCandidate(SyntaxNode node) =>
        node is InvocationExpressionSyntax { ArgumentList.Arguments.Count: > 0, Expression: var expression }
        && (expression switch
        {
            MemberAccessExpressionSyntax access => access.Name,
            MemberBindingExpressionSyntax binding => binding.Name,
            _ => null,
        })?.Identifier.ValueText is "Send" or "Publish" or "CreateStream";

    /// <summary>
    /// The call, when it is made on <c>ISender</c>, <c>IPublisher</c>, <c>IMediator</c> or a class implementing one of
    /// them, and the message's static type is known. A message passed as <c>object</c> is left out: the map cannot
    /// tell what it is.
    /// </summary>
    public static CallSite? Read(GeneratorSyntaxContext context, CancellationToken cancellationToken)
    {
        InvocationExpressionSyntax invocation = (InvocationExpressionSyntax)context.Node;
        ArgumentSyntax? argument;
        if (context.SemanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is IMethodSymbol method)
        {
            if (method.Parameters.Length == 0 || !IsMediator(method.ContainingType))
            {
                return null;
            }

            // The message is the first parameter, wherever a named argument puts it.
            argument = invocation.ArgumentList.Arguments.FirstOrDefault(argument => argument.NameColon?.Name.Identifier.ValueText == method.Parameters[0].Name)
                ?? First(invocation);
        }
        else
        {
            // The generated RelevoxMediator is not written yet when this runs, so a call on it binds to nothing. Its
            // methods take the message first, named request or notification.
            argument = Owner(invocation) is { } owner
                && context.SemanticModel.GetTypeInfo(owner, cancellationToken).Type is IErrorTypeSymbol { Name: "RelevoxMediator" }
                ? invocation.ArgumentList.Arguments.FirstOrDefault(static argument => argument.NameColon?.Name.Identifier.ValueText is "request" or "notification")
                    ?? First(invocation)
                : null;
        }

        ITypeSymbol? message = argument is null ? null : context.SemanticModel.GetTypeInfo(argument.Expression, cancellationToken).Type;
        if (message is null or IErrorTypeSymbol or ITypeParameterSymbol || message.SpecialType == SpecialType.System_Object)
        {
            return null;
        }

        // The method name's line, which for a call chained over several lines is the line that sends.
        SyntaxNode name = invocation.Expression switch
        {
            MemberAccessExpressionSyntax access => access.Name,
            MemberBindingExpressionSyntax binding => binding.Name,
            _ => invocation,
        };
        return new CallSite(
            message.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", ""),
            invocation.SyntaxTree.FilePath,
            name.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
    }

    /// <summary>
    /// The file relative to the project, with forward slashes, so the map is the same on every machine and never
    /// holds an absolute path (ADR 0014). A file outside the project, such as a linked one, is written by its name.
    /// </summary>
    public static string Relative(string file, string? projectDirectory)
    {
        string relative = !string.IsNullOrEmpty(projectDirectory) && file.StartsWith(projectDirectory, System.StringComparison.OrdinalIgnoreCase)
            ? file.Substring(projectDirectory!.Length).TrimStart('\\', '/')
            // By hand rather than Path.GetFileName, which only splits on the separators of the machine it runs on.
            : file.Substring(file.LastIndexOfAny(['\\', '/']) + 1);
        return relative.Replace('\\', '/');
    }

    private static ArgumentSyntax? First(InvocationExpressionSyntax invocation) =>
        invocation.ArgumentList.Arguments[0].NameColon is null ? invocation.ArgumentList.Arguments[0] : null;

    private static ExpressionSyntax? Owner(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax access => access.Expression,
        MemberBindingExpressionSyntax binding => binding.FirstAncestorOrSelf<ConditionalAccessExpressionSyntax>()?.Expression,
        _ => null,
    };

    private static bool IsMediator(INamedTypeSymbol? type) =>
        type is not null && (IsContract(type) || type.AllInterfaces.Any(IsContract));

    private static bool IsContract(INamedTypeSymbol type) =>
        type.ContainingNamespace is { Name: "Relevox", ContainingNamespace.IsGlobalNamespace: true }
        && type.Name is "ISender" or "IPublisher" or "IMediator";
}
