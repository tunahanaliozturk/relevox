using System;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Relevox.Migration;

/// <summary>
/// Which generated code the migration analyzers leave alone: what a tool writes, not what a person keeps.
/// </summary>
/// <remarks>
/// <para>
/// The analyzers ask Roslyn for generated code too, because scaffolding tools write an <c>&lt;auto-generated&gt;</c>
/// header on code that is then kept by hand, and the move has to reach it. What a generator writes is another
/// matter: nobody can edit it, so a diagnostic there would only be noise, and a source generator that is still
/// referenced half way through the swap, or the Razor generator compiling an <c>@using Mediator</c>, writes plenty.
/// </para>
/// <para>
/// Roslyn's own rules, as <c>GeneratedCode</c> in the generator spells them out, without the header: the
/// <c>generated_code</c> setting in .editorconfig wins, and otherwise a file named <c>*.g.cs</c>, <c>*.g.i.cs</c>,
/// <c>*.designer.cs</c>, <c>*.generated.cs</c> or <c>TemporaryGeneratedFile_*</c> is a tool's. Source generators
/// name their files that way almost without exception; one that does not is reported like a scaffolded file.
/// </para>
/// </remarks>
internal static class GeneratedFiles
{
    private static readonly string[] Suffixes = [".designer", ".generated", ".g", ".g.i"];

    /// <summary>The action, run only on code outside files a tool wrote.</summary>
    /// <param name="action">The analyzer's action.</param>
    /// <returns>The action behind the check.</returns>
    public static Action<SyntaxNodeAnalysisContext> HandKept(Action<SyntaxNodeAnalysisContext> action) =>
        context =>
        {
            if (!WrittenByATool(context.Node.SyntaxTree, context.Compilation, context.CancellationToken))
            {
                action(context);
            }
        };

    private static bool WrittenByATool(SyntaxTree tree, Compilation compilation, CancellationToken cancellationToken) =>
        compilation.Options.SyntaxTreeOptionsProvider?.IsGenerated(tree, cancellationToken) switch
        {
            GeneratedKind.MarkedGenerated => true,
            GeneratedKind.NotGenerated => false,
            _ => IsGeneratedFileName(tree.FilePath),
        };

    private static bool IsGeneratedFileName(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        string name = Path.GetFileName(path);
        if (name.StartsWith("TemporaryGeneratedFile_", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string stem = Path.GetFileNameWithoutExtension(path);
        return Path.GetExtension(path).Length > 0
            && Suffixes.Any(suffix => stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }
}
