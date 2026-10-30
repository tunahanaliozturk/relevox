using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Relevox.CodeFixes;

/// <summary>
/// The using-directive rewrite both migration fixes share: rename the old library's namespace to Relevox without
/// disturbing the order, the duplicates or the trivia of the block.
/// </summary>
internal static class UsingsMover
{
    internal static IdentifierNameSyntax Renamed(IdentifierNameSyntax name, string to) =>
        name.WithIdentifier(SyntaxFactory.Identifier(to).WithTriviaFrom(name.Identifier));

    /// <summary>
    /// The file or namespace with every movable using of the old library in it renamed to <c>Relevox</c>, and kept in
    /// order if it was in order.
    /// </summary>
    /// <remarks>
    /// Renamed in place, <c>using MediatR;</c> becomes <c>using Relevox;</c> where MediatR sorted, before
    /// Microsoft, and a solution that checks its formatting fails after the migration. The whole block is redone
    /// for each using in it, so every fix for the block makes the same change and "Fix all" merges them as one.
    /// Shared by the MediatR and the Mediator fixes, which differ only in which names they call movable.
    /// </remarks>
    /// <param name="block">The compilation unit or namespace declaration holding the usings.</param>
    /// <param name="isMovable">Whether an identifier, as the root of a using's name, is renamed.</param>
    internal static SyntaxNode Moved(SyntaxNode block, Func<IdentifierNameSyntax, bool> isMovable)
    {
        SyntaxList<UsingDirectiveSyntax> usings = block switch
        {
            CompilationUnitSyntax unit => unit.Usings,
            BaseNamespaceDeclarationSyntax space => space.Usings,
            _ => default,
        };

        UsingDirectiveSyntax[] renamed =
        [
            .. usings.Select(directive => directive.ReplaceNodes(
                directive.NamespaceOrType.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
                    .Where(isMovable),
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
}
