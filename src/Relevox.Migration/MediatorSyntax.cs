using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Relevox.Migration;

/// <summary>
/// What the Mediator analyzer and its code fix both need to know about code written for the Mediator library,
/// read from syntax and names alone.
/// </summary>
/// <remarks>
/// The Mediator package is gone by the time either runs, so none of its names resolve and there are no symbols
/// to ask. A name counts as Mediator's when it is spelled <c>Mediator.X</c> with a root that does not resolve,
/// or when it is a simple name that resolves to nothing (or to Relevox, once a <c>using Relevox;</c> is in the
/// file). A simple name that resolves to the project's own type is left alone: a project that has its own
/// <c>ICommand&lt;T&gt;</c> is not asking to have it replaced.
/// </remarks>
public static class MediatorSyntax
{
    // Names in Mediator's namespace that Relevox has directly under Relevox, with the same meaning, so renaming
    // the root of Mediator.IMediator or Mediator.Unit is all that is needed.
    private static readonly HashSet<string> Mapped = new(System.StringComparer.Ordinal)
    {
        "IMediator", "ISender", "IPublisher", "IBaseRequest", "IRequest", "INotification", "IStreamRequest",
        "IRequestHandler", "INotificationHandler", "IStreamRequestHandler", "IPipelineBehavior", "IStreamPipelineBehavior", "Unit",
    };

    // Mediator names Relevox has no counterpart for. A type that names one is left for the compiler to point at,
    // since moving the rest of it would leave code that compiles against neither library.
    private static readonly HashSet<string> Unmappable = new(System.StringComparer.Ordinal)
    {
        "MessagePreProcessor", "MessagePostProcessor", "MessageExceptionHandler", "INotificationPublisher", "IMessage",
        "IStreamMessage", "IBaseCommand", "IBaseQuery", "IBaseStreamCommand", "IBaseStreamQuery", "IBaseStreamRequest",
    };

    /// <summary>A Mediator name as written: the part that names the type, and what it was qualified with.</summary>
    public readonly struct Name
    {
        /// <summary>Reads a name.</summary>
        /// <param name="simple">The simple name, with its type arguments if it has any.</param>
        /// <param name="qualifier">The part before the dot, rooted at <c>Mediator</c>, or null for a simple name.</param>
        /// <param name="resolved">True when a simple name resolved, to Relevox's type of that name.</param>
        public Name(SimpleNameSyntax simple, NameSyntax? qualifier, bool resolved)
        {
            Simple = simple;
            Qualifier = qualifier;
            Resolved = resolved;
        }

        /// <summary>The simple name, with its type arguments if it has any.</summary>
        public SimpleNameSyntax Simple { get; }

        /// <summary>The part before the dot, rooted at <c>Mediator</c>, or null for a simple name.</summary>
        public NameSyntax? Qualifier { get; }

        /// <summary>True when a simple name resolved, to Relevox's type of that name.</summary>
        public bool Resolved { get; }

        /// <summary>The identifier without type arguments.</summary>
        public string Text => Simple.Identifier.ValueText;

        /// <summary>The number of type arguments.</summary>
        public int Arity => Simple is GenericNameSyntax generic ? generic.TypeArgumentList.Arguments.Count : 0;
    }

    /// <summary>
    /// Whether <c>Mediator</c>, as this name, roots a name that Relevox has a counterpart for, so the code fix can
    /// move it by renaming the root.
    /// </summary>
    /// <param name="name">An identifier reading <c>Mediator</c>.</param>
    /// <returns>True when renaming it to <c>Relevox</c> keeps the code meaning the same thing.</returns>
    public static bool IsMovable(IdentifierNameSyntax name)
    {
        // Mediator has to be the root of the name: Company.Mediator.Foo is somebody else's namespace.
        switch (name.Parent)
        {
            case QualifiedNameSyntax qualified when qualified.Right == name:
            case MemberAccessExpressionSyntax access when access.Name == name:
            case AliasQualifiedNameSyntax alias when alias.Name == name && alias.Alias.Identifier.ValueText != "global":
                return false;
            default:
                break;
        }

        string? second = SecondPart(name);
        if (name.FirstAncestorOrSelf<UsingDirectiveSyntax>() is { } directive)
        {
            // A plain using names the namespace itself, an alias or a using static a type of it.
            return second is null ? directive.StaticKeyword.RawKind == 0 : Mapped.Contains(second);
        }

        // Outside a using the name is only worth moving when it leads to a type Relevox has.
        return second is not null && Mapped.Contains(second);
    }

    /// <summary>Whether the name binds to anything, which for <c>Mediator</c> means a namespace of the project's own.</summary>
    /// <param name="model">The semantic model of the file.</param>
    /// <param name="node">The name.</param>
    /// <param name="cancellationToken">The token to observe.</param>
    /// <returns>True when it resolves, or has candidates that might.</returns>
    public static bool Resolves(SemanticModel model, SyntaxNode node, CancellationToken cancellationToken)
    {
        SymbolInfo info = model.GetSymbolInfo(node, cancellationToken);
        return info.Symbol is not null || info.CandidateSymbols.Length > 0;
    }

    /// <summary>The part after <c>Mediator</c> in <c>Mediator.Unit</c> or <c>global::Mediator.Unit.Value</c>.</summary>
    /// <param name="name">An identifier reading <c>Mediator</c>.</param>
    /// <returns>The second identifier, or null when the name is not followed by a dot.</returns>
    public static string? SecondPart(IdentifierNameSyntax name)
    {
        SyntaxNode node = name.Parent is AliasQualifiedNameSyntax alias ? alias : name;
        return node.Parent switch
        {
            QualifiedNameSyntax qualified when qualified.Left == node => qualified.Right.Identifier.ValueText,
            MemberAccessExpressionSyntax access when access.Expression == node => access.Name.Identifier.ValueText,
            _ => null,
        };
    }

    /// <summary>
    /// Reads a type as a Mediator name, or returns null when it is something else: a type of the project's own,
    /// another library's, or a name rooted somewhere other than <c>Mediator</c>.
    /// </summary>
    /// <param name="type">The type as written.</param>
    /// <param name="model">The semantic model of the file.</param>
    /// <param name="cancellationToken">The token to observe.</param>
    /// <returns>The name, or null.</returns>
    public static Name? Read(TypeSyntax type, SemanticModel model, CancellationToken cancellationToken)
    {
        if (type is QualifiedNameSyntax { Left: var left, Right: var right })
        {
            // Mediator.X and global::Mediator.X, with a root that is not the project's own namespace.
            SimpleNameSyntax? root = left switch
            {
                IdentifierNameSyntax identifier => identifier,
                AliasQualifiedNameSyntax { Alias.Identifier.ValueText: "global", Name: var identifier } => identifier,
                _ => null,
            };

            return root is IdentifierNameSyntax rootName
                && ((rootName.Identifier.ValueText == "Mediator" && !Resolves(model, rootName, cancellationToken)) || IsMediatorAlias(rootName))
                ? new Name(right, left, resolved: false)
                : null;
        }

        if (type is not SimpleNameSyntax simple)
        {
            return null;
        }

        SymbolInfo info = model.GetSymbolInfo(simple, cancellationToken);
        ISymbol? symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
        if (symbol is null)
        {
            return new Name(simple, null, resolved: false);
        }

        return symbol.ContainingNamespace?.ToDisplayString() == "Relevox" ? new Name(simple, null, resolved: true) : null;
    }

    // using M = Mediator; in the file or an enclosing namespace. The fix renames the alias's target, so M.ICommand<T>
    // is still Mediator's to read and becomes M.IRequest<T>.
    private static bool IsMediatorAlias(IdentifierNameSyntax root)
    {
        string alias = root.Identifier.ValueText;
        return root.Ancestors()
            .SelectMany(static node => node switch
            {
                CompilationUnitSyntax unit => unit.Usings,
                BaseNamespaceDeclarationSyntax space => space.Usings,
                _ => default(SyntaxList<UsingDirectiveSyntax>),
            })
            .Any(directive => directive.Alias?.Name.Identifier.ValueText == alias
                && directive.NamespaceOrType is IdentifierNameSyntax { Identifier.ValueText: "Mediator" }
                    or AliasQualifiedNameSyntax { Alias.Identifier.ValueText: "global", Name.Identifier.ValueText: "Mediator" });
    }

    /// <summary>Whether the table in ADR 0024 rewrites this name, which is when it is not already Relevox's.</summary>
    /// <param name="name">The name and its number of type arguments.</param>
    /// <returns>True for commands, queries, void requests and their handlers and streams.</returns>
    public static bool IsMapped(Name name) => !name.Resolved && (name.Text, name.Arity) switch
    {
        ("ICommand", 0 or 1) or ("IQuery", 1) or ("IRequest", 0) => true,
        ("IStreamCommand", 1) or ("IStreamQuery", 1) => true,
        ("ICommandHandler", 1 or 2) or ("IQueryHandler", 2) or ("IRequestHandler", 1) => true,
        ("IStreamCommandHandler", 2) or ("IStreamQueryHandler", 2) => true,
        _ => false,
    };

    /// <summary>
    /// Whether the type is moved: its base list names something in the table, or it implements a handler or a
    /// behaviour and has a <c>Handle</c> method in Mediator's shape.
    /// </summary>
    /// <param name="declaration">The class, record, struct or interface.</param>
    /// <param name="model">The semantic model of the file.</param>
    /// <param name="cancellationToken">The token to observe.</param>
    /// <returns>True when the code fix rewrites the declaration.</returns>
    public static bool IsMigratable(TypeDeclarationSyntax declaration, SemanticModel model, CancellationToken cancellationToken)
    {
        if (declaration.BaseList is null)
        {
            return false;
        }

        bool shape = false;
        foreach (BaseTypeSyntax baseType in declaration.BaseList.Types)
        {
            if (Read(baseType.Type, model, cancellationToken) is not { } name)
            {
                continue;
            }

            if (Unmappable.Contains(name.Text))
            {
                return false;
            }

            if (IsMapped(name))
            {
                return true;
            }

            shape |= (name.Text, name.Arity) is ("IRequestHandler", 2) or ("INotificationHandler", 1) or ("IStreamRequestHandler", 2)
                or ("IPipelineBehavior", 2) or ("IStreamPipelineBehavior", 2);
        }

        return shape && declaration.Members.OfType<MethodDeclarationSyntax>().Any(method =>
            method.Identifier.ValueText == "Handle" && (ReturnsValueTask(method) || DelegateParameters(method, model, cancellationToken).Any()));
    }

    /// <summary>Whether the method returns <c>ValueTask</c> or <c>ValueTask&lt;T&gt;</c>, by whatever qualification.</summary>
    /// <param name="method">The method.</param>
    /// <returns>True when the return type's own name is ValueTask.</returns>
    public static bool ReturnsValueTask(MethodDeclarationSyntax method) =>
        method.ReturnType is SimpleNameSyntax { Identifier.ValueText: "ValueTask" }
            or QualifiedNameSyntax { Right.Identifier.ValueText: "ValueTask" };

    /// <summary>The parameters typed <c>MessageHandlerDelegate&lt;,&gt;</c> or <c>StreamHandlerDelegate&lt;,&gt;</c>.</summary>
    /// <param name="method">The method.</param>
    /// <param name="model">The semantic model of the file.</param>
    /// <param name="cancellationToken">The token to observe.</param>
    /// <returns>The parameters, in order.</returns>
    public static IEnumerable<ParameterSyntax> DelegateParameters(MethodDeclarationSyntax method, SemanticModel model, CancellationToken cancellationToken) =>
        method.ParameterList.Parameters.Where(parameter => parameter.Type is { } type
            && Read(type, model, cancellationToken) is { Arity: 2, Text: "MessageHandlerDelegate" or "StreamHandlerDelegate" });
}
