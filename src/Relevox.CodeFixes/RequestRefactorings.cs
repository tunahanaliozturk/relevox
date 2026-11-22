using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Simplification;

namespace Relevox.CodeFixes;

/// <summary>
/// Refactorings offered on a type's header that write the other half of a request and its handler.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>On a class or record that is not a message yet: make it a request, alone or with its handler.</item>
/// <item>On a request with no handler in this project: add the handler, as the RVX0001 fix does.</item>
/// <item>On a handler whose request or notification type does not exist yet: write that type.</item>
/// </list>
/// <para>
/// The second repeats the RVX0001 fix because that rule is reported once the whole compilation has been
/// analysed, which the IDE does not do as you type in a file unless analysis covers the whole solution. The
/// refactoring needs only the file that is open. ADR 0028 has the reasons.
/// </para>
/// </remarks>
[ExportCodeRefactoringProvider(LanguageNames.CSharp, Name = nameof(RequestRefactorings))]
[Shared]
public sealed class RequestRefactorings : CodeRefactoringProvider
{
    /// <inheritdoc />
    public override async Task ComputeRefactoringsAsync(CodeRefactoringContext context)
    {
        Document document = context.Document;
        CancellationToken cancellationToken = context.CancellationToken;
        SyntaxNode? root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        SemanticModel? model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null
            || model is null
            || !MessageFiles.Editable(document, root.SyntaxTree, model.Compilation, cancellationToken)
            || MessageFiles.HeaderAt(root, context.Span) is not { } declaration
            || model.GetDeclaredSymbol(declaration, cancellationToken) is not { } type
            || MessageFiles.Contracts(model.Compilation) is not { } contracts)
        {
            return;
        }

        if (MissingMessage(document, declaration, type, model, contracts, cancellationToken) is { } writeMessage)
        {
            context.RegisterRefactoring(writeMessage);
        }

        if (type.AllInterfaces.Any(candidate => MessageFiles.IsContract(candidate, contracts)))
        {
            if (MessageFiles.RequestInterface(type, contracts) is not null
                && !HasHandler(model.Compilation, type, contracts, cancellationToken)
                && MessageFiles.AddHandler(document, type, model.Compilation) is { } addHandler)
            {
                context.RegisterRefactoring(addHandler);
            }

            return;
        }

        if (!MessageFiles.CanHaveHandler(type, model.Compilation))
        {
            return;
        }

        string handler = MessageFiles.FreeName(document, type.ContainingNamespace, type.Name + "Handler", typeToo: true);
        context.RegisterRefactoring(CodeAction.Create(
            $"Make '{type.Name}' a request",
            token => MakeRequestAsync(document, declaration, null, token),
            equivalenceKey: "MakeRequest"));
        context.RegisterRefactoring(CodeAction.Create(
            $"Make '{type.Name}' a request with handler '{handler}'",
            token => MakeRequestAsync(document, declaration, handler, token),
            equivalenceKey: "MakeRequestWithHandler"));
    }

    /// <summary>
    /// Adds <c>IRequest</c> to the type's base list and, when <paramref name="handler"/> is given, writes a
    /// handler of that name. <c>IRequest</c> has no response; the person changes it to <c>IRequest&lt;T&gt;</c>
    /// when there is one, since nothing here can know <c>T</c>.
    /// </summary>
    private static async Task<Solution> MakeRequestAsync(Document document, TypeDeclarationSyntax declaration, string? handler, CancellationToken cancellationToken)
    {
        Solution unchanged = document.Project.Solution;
        if (await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is not { } root)
        {
            return unchanged;
        }

        SyntaxAnnotation marker = new();
        TypeSyntax request = SyntaxFactory.ParseTypeName("global::Relevox.IRequest")
            .WithAdditionalAnnotations(Simplifier.Annotation, Simplifier.AddImportsAnnotation);

        document = document.WithSyntaxRoot(root.ReplaceNode(declaration, WithInterface(declaration, request).WithAdditionalAnnotations(marker)));
        document = await MessageFiles.ImportAndSimplifyAsync(document, cancellationToken).ConfigureAwait(false);
        if (handler is null)
        {
            return document.Project.Solution;
        }

        // Found again in the changed document, as the request it now is. Anything unexpected writes no handler.
        SyntaxNode? changedRoot = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        SemanticModel? model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        return changedRoot?.GetAnnotatedNodes(marker).FirstOrDefault() is { } changed
            && model?.GetDeclaredSymbol(changed, cancellationToken) is INamedTypeSymbol type
            && MessageFiles.Contracts(model.Compilation) is { } contracts
            && MessageFiles.RequestInterface(type, contracts) is { } contract
                ? await MessageFiles.AddHandlerAsync(document, type, contract, handler, cancellationToken).ConfigureAwait(false)
                : document.Project.Solution;
    }

    /// <summary>Appends <paramref name="added"/> to the base list, moving what followed the header after it.</summary>
    private static TypeDeclarationSyntax WithInterface(TypeDeclarationSyntax declaration, TypeSyntax added)
    {
        SyntaxToken comma = SyntaxFactory.Token(SyntaxFactory.TriviaList(), SyntaxKind.CommaToken, SyntaxFactory.TriviaList(SyntaxFactory.Space));
        if (declaration.BaseList is { } list)
        {
            BaseTypeSyntax last = list.Types.Last();
            SeparatedSyntaxList<BaseTypeSyntax> types = SyntaxFactory.SeparatedList<BaseTypeSyntax>(list.Types
                .Replace(last, last.WithoutTrailingTrivia())
                .GetWithSeparators()
                .Add(comma)
                .Add(SyntaxFactory.SimpleBaseType(added.WithTrailingTrivia(last.GetTrailingTrivia()))));
            return declaration.WithBaseList(list.WithTypes(types));
        }

        SyntaxToken end = declaration.ParameterList?.CloseParenToken ?? declaration.TypeParameterList?.GreaterThanToken ?? declaration.Identifier;
        SyntaxToken colon = SyntaxFactory.Token(SyntaxFactory.TriviaList(SyntaxFactory.Space), SyntaxKind.ColonToken, SyntaxFactory.TriviaList(SyntaxFactory.Space));
        return declaration
            .ReplaceToken(end, end.WithTrailingTrivia())
            .WithBaseList(SyntaxFactory.BaseList(colon, SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(
                SyntaxFactory.SimpleBaseType(added.WithTrailingTrivia(end.TrailingTrivia)))));
    }

    /// <summary>
    /// Whether a type in this project handles <paramref name="request"/> in a way the generator dispatches to: a
    /// class that is not abstract and that generated code can call. A handler in a project that references this one
    /// is not seen, which only means the refactoring is offered where RVX0001 would not be.
    /// </summary>
    private static bool HasHandler(Compilation compilation, INamedTypeSymbol request, IAssemblySymbol contracts, CancellationToken cancellationToken) =>
        compilation.GetSymbolsWithName(static _ => true, SymbolFilter.Type, cancellationToken)
            .OfType<INamedTypeSymbol>()
            .Where(type => type is { TypeKind: TypeKind.Class, IsAbstract: false } && compilation.IsSymbolAccessibleWithin(type, compilation.Assembly))
            .Any(type => type.AllInterfaces.Any(contract => MessageFiles.IsContract(contract, contracts)
                && contract.MetadataName is "IRequestHandler`1" or "IRequestHandler`2" or "IStreamRequestHandler`2"
                && SymbolEqualityComparer.Default.Equals(contract.TypeArguments[0], request)));

    /// <summary>
    /// For a handler whose base list names a request or notification that does not exist, an action that writes
    /// it. Only a plain name is written: a generic or qualified one says more about where the type should go
    /// than a template can honour. A response that names one of the handler's type parameters is refused too, since
    /// the new type would not have them.
    /// </summary>
    private static CodeAction? MissingMessage(Document document, TypeDeclarationSyntax declaration, INamedTypeSymbol handler, SemanticModel model, IAssemblySymbol contracts, CancellationToken cancellationToken)
    {
        foreach (BaseTypeSyntax baseType in declaration.BaseList?.Types ?? default)
        {
            GenericNameSyntax? generic = baseType.Type switch
            {
                GenericNameSyntax plain => plain,
                QualifiedNameSyntax { Right: GenericNameSyntax right } => right,
                AliasQualifiedNameSyntax { Name: GenericNameSyntax aliased } => aliased,
                _ => null,
            };
            if (generic is null
                || model.GetTypeInfo(baseType.Type, cancellationToken).Type is not INamedTypeSymbol contract
                || !MessageFiles.IsContract(contract, contracts)
                || generic.TypeArgumentList.Arguments.Count != contract.TypeArguments.Length
                || generic.TypeArgumentList.Arguments[0] is not IdentifierNameSyntax missing
                || model.GetTypeInfo(missing, cancellationToken).Type is not { TypeKind: TypeKind.Error }
                || generic.TypeArgumentList.Arguments.Skip(1).SelectMany(static argument => argument.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
                    .Any(name => model.GetSymbolInfo(name, cancellationToken).Symbol is ITypeParameterSymbol))
            {
                continue;
            }

            string response = contract.TypeArguments.Length < 2
                ? string.Empty
                : contract.TypeArguments[1].TypeKind == TypeKind.Error
                    ? generic.TypeArgumentList.Arguments[1].ToString()
                    : contract.TypeArguments[1].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat
                        .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier));
            (string kind, string message) = contract.MetadataName switch
            {
                "IRequestHandler`2" => ("request", $"global::Relevox.IRequest<{response}>"),
                "IRequestHandler`1" => ("request", "global::Relevox.IRequest"),
                "IStreamRequestHandler`2" => ("stream request", $"global::Relevox.IStreamRequest<{response}>"),
                "INotificationHandler`1" => ("notification", "global::Relevox.INotification"),
                _ => (string.Empty, string.Empty),
            };
            if (kind.Length == 0)
            {
                continue;
            }

            // The name as written, so a keyword keeps its @; the file is named without it.
            string name = missing.Identifier.ValueText;
            string file = MessageFiles.FreeName(document, handler.ContainingNamespace, name, typeToo: false);
            string text = $"{MessageFiles.Visibility(handler)} sealed record {missing.Identifier.Text} : {message};";
            return CodeAction.Create(
                $"Add {kind} '{name}'",
                token => MessageFiles.AddFileAsync(document, handler.ContainingNamespace, file, text, token),
                equivalenceKey: "AddMessage");
        }

        return null;
    }
}
