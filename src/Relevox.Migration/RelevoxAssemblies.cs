using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Relevox.Migration;

/// <summary>
/// Relevox's own assemblies, which a moved documentation <c>cref</c> has to resolve into. Both migration analyzers
/// check against them, so that a <c>cref</c> is never moved onto a type of the application's own that happens to
/// have Relevox's name.
/// </summary>
internal static class RelevoxAssemblies
{
    /// <summary>The contracts, and the runtime that declares <c>RelevoxOptions</c> when it is referenced.</summary>
    /// <param name="compilation">The compilation.</param>
    /// <returns>The assemblies; empty when the contracts are not referenced.</returns>
    public static HashSet<IAssemblySymbol> Of(Compilation compilation)
    {
        HashSet<IAssemblySymbol> relevox = new(SymbolEqualityComparer.Default);
        foreach (string name in new[] { "Relevox.IBaseRequest", "Microsoft.Extensions.DependencyInjection.RelevoxOptions" })
        {
            if (compilation.GetTypeByMetadataName(name) is { } type)
            {
                relevox.Add(type.ContainingAssembly);
            }
        }

        return relevox;
    }

    /// <summary>Whether the symbol is declared in one of the assemblies, or for a namespace, partly declared there.</summary>
    /// <param name="relevox">The assemblies from <see cref="Of"/>.</param>
    /// <param name="symbol">What a <c>cref</c> resolved to.</param>
    /// <returns>True when the symbol is Relevox's.</returns>
    public static bool Hold(HashSet<IAssemblySymbol> relevox, ISymbol symbol) =>
        symbol is INamespaceSymbol space
            ? space.ConstituentNamespaces.Any(part => part.ContainingAssembly is { } assembly && relevox.Contains(assembly))
            : symbol.ContainingAssembly is { } owner && relevox.Contains(owner);
}
