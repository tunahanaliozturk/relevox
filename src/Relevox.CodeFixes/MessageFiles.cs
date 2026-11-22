using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Simplification;
using Microsoft.CodeAnalysis.Text;
using Relevox.Migration;

namespace Relevox.CodeFixes;

/// <summary>
/// Writes a handler for a request, or a message for a handler, in a new file next to the type it is for.
/// </summary>
/// <remarks>
/// <para>
/// The new type is written with every name in full, <c>global::</c> included, and then the workspace adds the
/// usings the file needs and shortens the names. That way a using is only added when the name is not already in
/// scope, through a global using for example, and a name that would clash with one of the project's own types
/// stays qualified.
/// </para>
/// <para>
/// The file goes in the folder of the type it is for, in the same namespace, with a file-scoped namespace if that
/// file has one. A name already taken, by a file in that folder or by a type in that namespace, gets a number.
/// </para>
/// </remarks>
internal static class MessageFiles
{
    private static readonly SymbolDisplayFormat Qualified = SymbolDisplayFormat.FullyQualifiedFormat
        .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    /// <summary>The assembly that declares Relevox's interfaces, or null when the project cannot see it.</summary>
    public static IAssemblySymbol? Contracts(Compilation compilation) =>
        compilation.GetTypeByMetadataName("Relevox.IBaseRequest")?.ContainingAssembly;

    public static bool IsContract(INamedTypeSymbol type, IAssemblySymbol contracts) =>
        SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, contracts);

    /// <summary>
    /// The request interface a type implements, when it implements exactly one: <c>IRequest</c>,
    /// <c>IRequest&lt;T&gt;</c> or <c>IStreamRequest&lt;T&gt;</c>.
    /// </summary>
    public static INamedTypeSymbol? RequestInterface(INamedTypeSymbol type, IAssemblySymbol contracts)
    {
        INamedTypeSymbol[] found =
        [
            .. type.AllInterfaces.Where(candidate => IsContract(candidate, contracts)
                && candidate.MetadataName is "IRequest" or "IRequest`1" or "IStreamRequest`1"),
        ];
        return found.Length == 1 ? found[0] : null;
    }

    /// <summary>
    /// Whether a handler can be written for the type in a file of its own: a class or struct that can be created,
    /// that code in another file can name, that can be a type argument, and that is not generic.
    /// </summary>
    /// <remarks>
    /// The generator does not report a generic request, since one is reached through its closed forms, and a
    /// generic handler for it is a design choice (ADR 0008) a template should not make. A file-local type cannot be
    /// named from another file, and a ref struct cannot be the handler interface's type argument.
    /// </remarks>
    public static bool CanHaveHandler(INamedTypeSymbol type, Compilation compilation)
    {
        if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct) || type.IsAbstract || type.IsStatic || type.IsRefLikeType
            || !compilation.IsSymbolAccessibleWithin(type, compilation.Assembly))
        {
            return false;
        }

        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
        {
            if (current.TypeParameters.Length > 0 || current.IsFileLocal)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The action that writes a handler for <paramref name="request"/>, or null when none can be written.</summary>
    public static CodeAction? AddHandler(Document document, INamedTypeSymbol request, Compilation compilation)
    {
        if (Contracts(compilation) is not { } contracts
            || RequestInterface(request, contracts) is not { } contract
            || !CanHaveHandler(request, compilation))
        {
            return null;
        }

        string name = FreeName(document, request.ContainingNamespace, request.Name + "Handler", typeToo: true);
        return CodeAction.Create(
            $"Add handler '{name}'",
            cancellationToken => AddHandlerAsync(document, request, contract, name, cancellationToken),
            equivalenceKey: "AddHandler");
    }

    public static Task<Solution> AddHandlerAsync(Document document, INamedTypeSymbol request, INamedTypeSymbol contract, string name, CancellationToken cancellationToken)
    {
        string requestType = request.ToDisplayString(Qualified);
        string response = contract.TypeArguments.Length == 1 ? contract.TypeArguments[0].ToDisplayString(Qualified) : string.Empty;

        // Task, never ValueTask: Relevox keeps MediatR 12's handler shapes (ADR 0023).
        (string handler, string returns) = contract.MetadataName switch
        {
            "IRequest" => ($"global::Relevox.IRequestHandler<{requestType}>", "global::System.Threading.Tasks.Task"),
            "IRequest`1" => ($"global::Relevox.IRequestHandler<{requestType}, {response}>", $"global::System.Threading.Tasks.Task<{response}>"),
            _ => ($"global::Relevox.IStreamRequestHandler<{requestType}, {response}>", $"global::System.Collections.Generic.IAsyncEnumerable<{response}>"),
        };

        string declaration = $$"""
            {{Visibility(request)}} sealed class {{name}} : {{handler}}
            {
                public {{returns}} Handle({{requestType}} request, global::System.Threading.CancellationToken cancellationToken)
                {
                    throw new global::System.NotImplementedException();
                }
            }
            """;
        return AddFileAsync(document, request.ContainingNamespace, name, declaration, cancellationToken);
    }

    /// <summary>
    /// "public" when code outside the assembly can name the type, otherwise "internal", so a new type is as
    /// visible as the one it is written for.
    /// </summary>
    public static string Visibility(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility != Accessibility.Public)
            {
                return "internal";
            }
        }

        return "public";
    }

    /// <summary>
    /// <paramref name="name"/>, or the first of name2, name3 and so on that no file in the folder of
    /// <paramref name="beside"/> and, when <paramref name="typeToo"/>, no type in <paramref name="space"/> has.
    /// </summary>
    public static string FreeName(Document beside, INamespaceSymbol space, string name, bool typeToo)
    {
        for (int number = 1; ; number++)
        {
            string candidate = number == 1 ? name : name + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if ((!typeToo || space.GetTypeMembers(candidate).IsEmpty)
                && !beside.Project.Documents.Any(document => string.Equals(document.Name, candidate + ".cs", StringComparison.OrdinalIgnoreCase)
                    && document.Folders.SequenceEqual(beside.Folders, StringComparer.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }
    }

    /// <summary>Adds <paramref name="declaration"/>, written with names in full, as <c>fileName.cs</c> next to <paramref name="beside"/>.</summary>
    public static async Task<Solution> AddFileAsync(Document beside, INamespaceSymbol space, string fileName, string declaration, CancellationToken cancellationToken)
    {
        if (await beside.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is not { } besideRoot)
        {
            return beside.Project.Solution;
        }

        SourceText besideText = await beside.GetTextAsync(cancellationToken).ConfigureAwait(false);

        string text = space.IsGlobalNamespace
            ? declaration
            : besideRoot.DescendantNodes().OfType<FileScopedNamespaceDeclarationSyntax>().Any()
                ? $"namespace {space.ToDisplayString()};\n\n{declaration}"
                : $"namespace {space.ToDisplayString()}\n{{\n{declaration}\n}}";

        string? path = beside.FilePath is { } besidePath && System.IO.Path.GetDirectoryName(besidePath) is { } directory
            ? System.IO.Path.Combine(directory, fileName + ".cs")
            : null;
        Document document = beside.Project.AddDocument(fileName + ".cs", SourceText.From(text + "\n"), beside.Folders, path);

        if (await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is not { } root)
        {
            return beside.Project.Solution;
        }

        root = root.ReplaceNodes(
            root.DescendantNodes().OfType<NameSyntax>().Where(static name => name.Parent is not (NameSyntax or BaseNamespaceDeclarationSyntax)),
            static (_, name) => name.WithAdditionalAnnotations(Simplifier.Annotation, Simplifier.AddImportsAnnotation));
        document = document.WithSyntaxRoot(root);

        document = await ImportAndSimplifyAsync(document, cancellationToken).ConfigureAwait(false);
        document = await Formatter.FormatAsync(document, cancellationToken: cancellationToken).ConfigureAwait(false);

        // The new file ends its lines the way the file next to it does, whatever the formatter's default is.
        string written = (await document.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString().Replace("\r\n", "\n");
        if (NewLine(besideText) is { } newLine && newLine != "\n")
        {
            written = written.Replace("\n", newLine);
        }

        return document.WithText(SourceText.From(written, besideText.Encoding)).Project.Solution;
    }

    /// <summary>
    /// Adds the usings that names marked with <see cref="Simplifier.AddImportsAnnotation"/> need, shortens the names
    /// marked with <see cref="Simplifier.Annotation"/>, and takes the import mark off.
    /// </summary>
    /// <remarks>
    /// The import step does not see global usings, so it can add a using that simplifying then removes again,
    /// leaving a blank line behind. When the usings come out the same as they went in, the document is only
    /// simplified. The mark comes off so that the IDE's clean-up after an action does not run the step again.
    /// </remarks>
    public static async Task<Document> ImportAndSimplifyAsync(Document document, CancellationToken cancellationToken)
    {
        Document imported = await ImportAdder.AddImportsAsync(document, Simplifier.AddImportsAnnotation, cancellationToken: cancellationToken).ConfigureAwait(false);
        imported = await Simplifier.ReduceAsync(imported, Simplifier.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (await Usings(imported, cancellationToken).ConfigureAwait(false) == await Usings(document, cancellationToken).ConfigureAwait(false))
        {
            imported = await Simplifier.ReduceAsync(document, Simplifier.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        if (await imported.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is not { } root)
        {
            return imported;
        }

        return imported.WithSyntaxRoot(root.ReplaceNodes(
            root.GetAnnotatedNodes(Simplifier.AddImportsAnnotation),
            static (_, node) => node.WithoutAnnotations(Simplifier.AddImportsAnnotation)));

        static async Task<int> Usings(Document document, CancellationToken cancellationToken) =>
            (await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false))?.DescendantNodes().OfType<UsingDirectiveSyntax>().Count() ?? 0;
    }

    private static string? NewLine(SourceText text)
    {
        foreach (TextLine line in text.Lines)
        {
            if (line.EndIncludingLineBreak > line.End)
            {
                return text.ToString(TextSpan.FromBounds(line.End, line.EndIncludingLineBreak));
            }
        }

        return null;
    }

    /// <summary>
    /// Whether the actions may change code in the document: not a source generator's output, and not a file a tool
    /// writes, by the rules the migration analyzers use.
    /// </summary>
    public static bool Editable(Document document, SyntaxTree tree, Compilation compilation, CancellationToken cancellationToken) =>
        document is not SourceGeneratedDocument && !GeneratedFiles.WrittenByATool(tree, compilation, cancellationToken);

    /// <summary>The type declaration whose header, from its modifiers to its base list, holds the span.</summary>
    public static TypeDeclarationSyntax? HeaderAt(SyntaxNode root, TextSpan span)
    {
        TypeDeclarationSyntax? declaration = root.FullSpan.Contains(span)
            ? root.FindToken(span.Start).Parent?.FirstAncestorOrSelf<TypeDeclarationSyntax>()
            : null;
        if (declaration is null || declaration.Identifier.IsMissing)
        {
            return null;
        }

        int start = declaration.Modifiers.Count > 0 ? declaration.Modifiers[0].SpanStart : declaration.Keyword.SpanStart;
        int end = declaration.BaseList?.Span.End
            ?? declaration.ParameterList?.Span.End
            ?? declaration.TypeParameterList?.Span.End
            ?? declaration.Identifier.Span.End;
        return span.Start >= start && span.End <= end ? declaration : null;
    }
}
