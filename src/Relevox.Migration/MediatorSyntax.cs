using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

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
    public static Name? Read(TypeSyntax type, SemanticModel model, CancellationToken cancellationToken) =>
        type switch
        {
            QualifiedNameSyntax { Left: var left, Right: var right } => Read(right, left, model, cancellationToken),
            SimpleNameSyntax simple => Read(simple, null, model, cancellationToken),
            _ => null,
        };

    private static Name? Read(SimpleNameSyntax simple, NameSyntax? qualifier, SemanticModel model, CancellationToken cancellationToken)
    {
        if (qualifier is not null)
        {
            // Mediator.X and global::Mediator.X, with a root that is not the project's own namespace.
            SimpleNameSyntax? root = qualifier switch
            {
                IdentifierNameSyntax identifier => identifier,
                AliasQualifiedNameSyntax { Alias.Identifier.ValueText: "global", Name: var identifier } => identifier,
                _ => null,
            };

            return root is IdentifierNameSyntax rootName
                && ((rootName.Identifier.ValueText == "Mediator" && !Resolves(model, rootName, cancellationToken)) || IsMediatorAlias(rootName))
                ? new Name(simple, qualifier, resolved: false)
                : null;
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
        return Usings(root).Any(directive => directive.Alias?.Name.Identifier.ValueText == alias && NamesMediator(directive));
    }

    // The using directives of the file and the namespaces around the node.
    private static IEnumerable<UsingDirectiveSyntax> Usings(SyntaxNode node) =>
        node.Ancestors().SelectMany(static ancestor => ancestor switch
        {
            CompilationUnitSyntax unit => unit.Usings,
            BaseNamespaceDeclarationSyntax space => space.Usings,
            _ => default(SyntaxList<UsingDirectiveSyntax>),
        });

    // using ... Mediator; or using ... global::Mediator;
    private static bool NamesMediator(UsingDirectiveSyntax directive) =>
        directive.NamespaceOrType is IdentifierNameSyntax { Identifier.ValueText: "Mediator" }
            or AliasQualifiedNameSyntax { Alias.Identifier.ValueText: "global", Name.Identifier.ValueText: "Mediator" };

    /// <summary>
    /// A documentation <c>cref</c> with its Mediator names moved as the code around it is moved, or null when it is
    /// left as it is. Shared by the analyzer, which reports it, and the code fix, which writes it inside a type it
    /// moves as well as on its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Mediator</c> at the root of a name Relevox has becomes <c>Relevox</c>, and a name in ADR 0024's table
    /// becomes its row's, as a generic type definition: <c>ICommand{TResponse}</c> becomes
    /// <c>IRequest{TResponse}</c>, and a void request or handler gains a type parameter called <c>Unit</c>, so
    /// <c>ICommandHandler{TCommand}</c> becomes <c>IRequestHandler{TCommand, Unit}</c>. Type arguments in a
    /// <c>cref</c> name type parameters, not types, so that is the definition the code now implements.
    /// </para>
    /// <para>
    /// The <c>cref</c> is moved as a whole, and only when the result resolves into Relevox's own assemblies. A
    /// simple name such as <c>IRequest{TResponse}</c> resolves only after the fix turns <c>using Mediator;</c> into
    /// <c>using Relevox;</c>, so it is checked as <c>global::Relevox.IRequest{TResponse}</c>, and only where such a
    /// using is in scope and nothing else of that name is. Anything else is left for the compiler's CS1574: a
    /// <c>cref</c> to a Mediator type Relevox lacks, to a member Relevox's type does not have, or one that would
    /// resolve to a type of the application's own.
    /// </para>
    /// </remarks>
    /// <param name="cref">A <c>cref</c> from a documentation comment, in the tree the model is for.</param>
    /// <param name="model">The semantic model of the file.</param>
    /// <param name="cancellationToken">The token to observe.</param>
    /// <returns>The moved <c>cref</c>, or null.</returns>
    public static CrefSyntax? MovedCref(CrefSyntax cref, SemanticModel model, CancellationToken cancellationToken) =>
        MovedCref(cref, model, RelevoxAssemblies.Of(model.Compilation), cancellationToken);

    internal static CrefSyntax? MovedCref(CrefSyntax cref, SemanticModel model, HashSet<IAssemblySymbol> relevox, CancellationToken cancellationToken)
    {
        // A cref that does not parse as a whole is left whole: moving the part that parsed would not fix it.
        SyntaxNode attribute = cref.Parent ?? cref;
        if (attribute.ContainsSkippedText || attribute.ContainsDiagnostics || Resolves(model, cref, cancellationToken))
        {
            return null;
        }

        // Edits by span: what the cref becomes, and the probe that checks it with simple names qualified.
        Dictionary<TextSpan, string> moved = new();
        Dictionary<TextSpan, string> probe = new();
        bool shortNames = false;
        foreach (SimpleNameSyntax simple in cref.DescendantNodes().OfType<SimpleNameSyntax>())
        {
            // Type arguments in a cref declare type parameters; they are not names to move.
            if (simple.Ancestors().TakeWhile(node => node != cref).Any(static node => node is TypeArgumentListSyntax)
                || (simple.Parent is NameMemberCrefSyntax { Parent: QualifiedCrefSyntax { Container: not NameSyntax } })
                || Read(simple, Qualifier(simple), model, cancellationToken) is not { } name)
            {
                continue;
            }

            string written;
            if (IsMapped(name))
            {
                (string target, bool unit) = Counterpart(name);
                IEnumerable<string> parameters = name.Simple is GenericNameSyntax generic ? generic.TypeArgumentList.Arguments.Select(static argument => argument.ToString()) : [];
                parameters = unit ? parameters.Append("Unit") : parameters;
                written = parameters.Any() ? $"{target}{{{string.Join(", ", parameters)}}}" : target;
                moved[simple.Span] = written;
            }
            else if (!name.Resolved && Mapped.Contains(name.Text))
            {
                written = simple.ToString();
            }
            else
            {
                continue;
            }

            if (name.Qualifier is IdentifierNameSyntax alias && alias.Identifier.ValueText != "Mediator")
            {
                // An alias of Mediator, which the fix points at Relevox: the cref keeps it, the probe names its target.
                probe[alias.Span] = "global::Relevox";
            }
            else if (name.Qualifier is { } qualifier)
            {
                foreach (IdentifierNameSyntax root in qualifier.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Where(static root => root.Identifier.ValueText == "Mediator"))
                {
                    moved[root.Span] = "Relevox";
                }
            }
            else if (model.LookupNamespacesAndTypes(cref.SpanStart, name: Identifier(written)).IsEmpty)
            {
                probe[simple.Span] = "global::Relevox." + written;
                shortNames = true;
            }
        }

        if (moved.Count == 0 || (shortNames && !ImportsMediator(cref, model.Compilation, cancellationToken)))
        {
            return null;
        }

        foreach (KeyValuePair<TextSpan, string> edit in moved)
        {
            if (!probe.ContainsKey(edit.Key))
            {
                probe[edit.Key] = edit.Value;
            }
        }

        return model.GetSpeculativeSymbolInfo(cref.SpanStart, Edited(cref, probe), SpeculativeBindingOption.BindAsTypeOrNamespace).Symbol is { } symbol
            && RelevoxAssemblies.Hold(relevox, symbol)
                ? Edited(cref, moved)
                : null;

        static string Identifier(string written) => written.Split('{')[0];
    }

    // What a simple name in a cref is qualified with: Mediator in Mediator.IRequest{T}, which the parser reads as a
    // member IRequest{T} of a container Mediator, as well as in a parameter's Mediator.IRequest{T}.
    private static NameSyntax? Qualifier(SimpleNameSyntax simple) => simple.Parent switch
    {
        QualifiedNameSyntax qualified when qualified.Right == simple => qualified.Left,
        NameMemberCrefSyntax { Parent: QualifiedCrefSyntax { Container: NameSyntax container } } => container,
        _ => null,
    };

    // The cref's text with the edits made, parsed again as a cref.
    private static CrefSyntax Edited(CrefSyntax cref, Dictionary<TextSpan, string> edits)
    {
        string text = SourceText.From(cref.ToString())
            .WithChanges(edits.Select(edit => new TextChange(new TextSpan(edit.Key.Start - cref.SpanStart, edit.Key.Length), edit.Value)))
            .ToString();
        return SyntaxFactory.ParseCompilationUnit($"/// <see cref=\"{text}\"/>\nclass C {{ }}", options: CSharpParseOptions.Default.WithDocumentationMode(DocumentationMode.Parse))
            .DescendantNodes(descendIntoTrivia: true)
            .OfType<XmlCrefAttributeSyntax>()
            .Single()
            .Cref;
    }

    // Whether a plain using of Mediator, which the fix turns into one of Relevox, covers the node: in its file or a
    // namespace around it, or as a global using anywhere in the project.
    private static bool ImportsMediator(SyntaxNode node, Compilation compilation, CancellationToken cancellationToken) =>
        Usings(node)
            .Concat(compilation.SyntaxTrees.SelectMany(tree => tree.GetCompilationUnitRoot(cancellationToken).Usings.Where(static directive => directive.GlobalKeyword.RawKind != 0)))
            .Any(static directive => directive is { Alias: null, StaticKeyword.RawKind: 0 } && NamesMediator(directive));

    /// <summary>The row of ADR 0024's table for a name in it.</summary>
    /// <param name="name">A name for which <see cref="IsMapped"/> is true.</param>
    /// <returns>Relevox's name, and whether <c>Unit</c> is added as its last type argument.</returns>
    public static (string Name, bool AddsUnit) Counterpart(Name name)
    {
        bool handler = name.Text.EndsWith("Handler", System.StringComparison.Ordinal);
        string target = name.Text.StartsWith("IStream", System.StringComparison.Ordinal)
            ? (handler ? "IStreamRequestHandler" : "IStreamRequest")
            : (handler ? "IRequestHandler" : "IRequest");
        return (target, name.Arity == 0 || (handler && name.Arity == 1));
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
