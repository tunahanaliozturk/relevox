using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Capar.SourceGenerator;

/// <summary>
/// What the generator reports instead of letting it fail at run time.
/// </summary>
/// <remarks>
/// A reflection-based mediator can only report these when the request is actually sent, which in practice
/// means in production, on the one path nobody exercised. Reporting them during the build is most of the
/// argument for dispatching at compile time at all.
/// </remarks>
internal static class Diagnostics
{
    private const string Category = "Capar";

    /// <summary>
    /// A warning, not an error: a request can legitimately exist before its handler does, and failing the
    /// build halfway through a refactoring would only get the rule switched off.
    /// </summary>
    public static readonly DiagnosticDescriptor NoHandler = new(
        id: "CPR0001",
        title: "Request has no handler",
        messageFormat: "'{0}' has no handler in this project or the projects it references, so sending it would throw",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Every request and stream request needs exactly one handler. This one has none that the generator can see, so sending it compiles and then fails when it runs.");

    /// <summary>
    /// An error: with two handlers there is no right answer, and picking one would make behaviour depend on
    /// the order the compiler happened to visit files in.
    /// </summary>
    public static readonly DiagnosticDescriptor AmbiguousHandler = new(
        id: "CPR0002",
        title: "Request has more than one handler",
        messageFormat: "'{0}' has {1} handlers ({2}), and a request needs exactly one",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A request or stream request is handled by exactly one handler. Use a notification when several handlers should all run.");

    public static readonly DiagnosticDescriptor Inaccessible = new(
        id: "CPR0003",
        title: "Handler cannot be reached from generated code",
        messageFormat: "'{0}' {1}, so the generated mediator cannot call it and it will not be registered",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The generated mediator is ordinary C# in this assembly, so it can only call handlers this assembly can see. Make the handler public or internal, or add InternalsVisibleTo for this assembly.");

    public static readonly DiagnosticDescriptor GenericHandler = new(
        id: "CPR0004",
        title: "Generic handler cannot be registered",
        messageFormat: "'{0}' is generic, and the generator only registers handlers whose type arguments are known at build time",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "MediatR can close a generic handler at run time. The generator cannot, because it writes dispatch for types it can see. Declare a closed handler per request instead.");

    public static Location ToLocation(LocationModel? model) =>
        model is null
            ? Location.None
            : Location.Create(
                model.FilePath,
                new TextSpan(model.Start, model.Length),
                new LinePositionSpan(
                    new LinePosition(model.StartLine, model.StartCharacter),
                    new LinePosition(model.EndLine, model.EndCharacter)));
}
