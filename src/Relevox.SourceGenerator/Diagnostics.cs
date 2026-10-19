using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Relevox.SourceGenerator;

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
    private const string Category = "Relevox";

    // Each rule links to its section of docs/diagnostics.md, which says why it exists and how to fix it.
    private const string Help = "https://github.com/tunahanaliozturk/relevox/blob/main/docs/diagnostics.md#";

    // Every rule is reported once the whole compilation has been seen, because whether a request has one
    // handler, none or two is only known then. Roslyn requires such rules to say so.
    private static readonly string[] CompilationEnd = [WellKnownDiagnosticTags.CompilationEnd];

    /// <summary>
    /// A warning, not an error: a request can legitimately exist before its handler does, and failing the
    /// build halfway through a refactoring would only get the rule switched off.
    /// </summary>
    public static readonly DiagnosticDescriptor NoHandler = new(
        id: "RVX0001",
        title: "Request has no handler",
        messageFormat: "'{0}' has no handler in this project or the projects it references, so sending it would throw",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        customTags: CompilationEnd,
        helpLinkUri: Help + "rvx0001",
        description: "Every request and stream request needs exactly one handler. This one has none that the generator can see, so sending it compiles and then fails when it runs.");

    /// <summary>
    /// An error: with two handlers there is no right answer, and picking one would make behaviour depend on
    /// the order the compiler happened to visit files in.
    /// </summary>
    public static readonly DiagnosticDescriptor AmbiguousHandler = new(
        id: "RVX0002",
        title: "Request has more than one handler",
        messageFormat: "'{0}' has {1} handlers ({2}), and a request needs exactly one",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        customTags: CompilationEnd,
        helpLinkUri: Help + "rvx0002",
        description: "A request or stream request is handled by exactly one handler. Use a notification when several handlers should all run.");

    /// <summary>
    /// An error: the handler exists, the user wrote it to be called, and without this it would silently not
    /// be, which is exactly the run-time surprise compile-time dispatch is meant to remove.
    /// </summary>
    public static readonly DiagnosticDescriptor Inaccessible = new(
        id: "RVX0003",
        title: "Handler cannot be called from generated code",
        messageFormat: "'{0}' {1}, so the generated mediator cannot call it and it is not registered",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        customTags: CompilationEnd,
        helpLinkUri: Help + "rvx0003",
        description: "The generated mediator is ordinary C# in this assembly, so it can only call handlers this assembly can see. Make the handler public, or add InternalsVisibleTo for this assembly.");

    public static readonly DiagnosticDescriptor GenericHandler = new(
        id: "RVX0004",
        title: "Generic handler cannot be registered",
        messageFormat: "'{0}' is a generic handler the generator cannot close: {1}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        customTags: CompilationEnd,
        helpLinkUri: Help + "rvx0004",
        description: "The generator writes dispatch for closed types, so a generic handler is registered by closing it at build time: a notification handler declared as H<T> : INotificationHandler<T> over every notification it can see, and a request or stream handler over the types that satisfy its constraints. Anything else is reported here; declare a closed handler per request instead.");

    /// <summary>
    /// A warning, raised only when the other assembly exposes public handlers. A library that uses Relevox
    /// privately keeps its handlers internal and is not reported.
    /// </summary>
    public static readonly DiagnosticDescriptor OwnMediator = new(
        id: "RVX0005",
        title: "Referenced project has its own mediator",
        messageFormat: "'{0}' references Relevox and has its own generated mediator, so its handlers ({1}) are not part of this one. If they belong to this application, reference Relevox.Contracts from '{0}' instead of Relevox.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        customTags: CompilationEnd,
        helpLinkUri: Help + "rvx0005",
        description: "Projects whose handlers belong to the application should reference Relevox.Contracts. Only the project that calls AddRelevox, usually the host, references Relevox itself.");

    /// <summary>
    /// A warning, because the call compiles and does something; it just registers a different project's
    /// handlers, which shows up as "no handler" only when a request is sent. Calling another project's
    /// <c>AddRelevox</c> on purpose is possible, which is what a suppression is for.
    /// </summary>
    public static readonly DiagnosticDescriptor ForeignRegistration = new(
        id: "RVX0006",
        title: "AddRelevox call registers another project's mediator",
        messageFormat: "This calls the AddRelevox generated for '{0}', which registers that project's mediator and handlers, not this project's. To register this project's, call {1}.AddRelevox(services).",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: Help + "rvx0006",
        description: "Each project that references Relevox gets its own AddRelevox. When another project's is visible too, through InternalsVisibleTo, a call can bind to it, and this project's handlers are then never registered.");

    public static readonly DiagnosticDescriptor[] All = [NoHandler, AmbiguousHandler, Inaccessible, GenericHandler, OwnMediator, ForeignRegistration];

    public static DiagnosticDescriptor Descriptor(string rule) => rule switch
    {
        "RVX0001" => NoHandler,
        "RVX0002" => AmbiguousHandler,
        "RVX0003" => Inaccessible,
        "RVX0004" => GenericHandler,
        "RVX0006" => ForeignRegistration,
        _ => OwnMediator,
    };

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
