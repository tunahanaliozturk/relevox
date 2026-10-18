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

    // Each rule links to its section of docs/diagnostics.md, which says why it exists and how to fix it.
    private const string Help = "https://github.com/tunahanaliozturk/capar/blob/main/docs/diagnostics.md#";

    // Every rule is reported once the whole compilation has been seen, because whether a request has one
    // handler, none or two is only known then. Roslyn requires such rules to say so.
    private static readonly string[] CompilationEnd = [WellKnownDiagnosticTags.CompilationEnd];

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
        customTags: CompilationEnd,
        helpLinkUri: Help + "cpr0001",
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
        customTags: CompilationEnd,
        helpLinkUri: Help + "cpr0002",
        description: "A request or stream request is handled by exactly one handler. Use a notification when several handlers should all run.");

    /// <summary>
    /// An error: the handler exists, the user wrote it to be called, and without this it would silently not
    /// be, which is exactly the run-time surprise compile-time dispatch is meant to remove.
    /// </summary>
    public static readonly DiagnosticDescriptor Inaccessible = new(
        id: "CPR0003",
        title: "Handler cannot be called from generated code",
        messageFormat: "'{0}' {1}, so the generated mediator cannot call it and it is not registered",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        customTags: CompilationEnd,
        helpLinkUri: Help + "cpr0003",
        description: "The generated mediator is ordinary C# in this assembly, so it can only call handlers this assembly can see. Make the handler public, or add InternalsVisibleTo for this assembly.");

    public static readonly DiagnosticDescriptor GenericHandler = new(
        id: "CPR0004",
        title: "Generic handler cannot be registered",
        messageFormat: "'{0}' is a generic handler the generator cannot close: {1}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        customTags: CompilationEnd,
        helpLinkUri: Help + "cpr0004",
        description: "The generator writes dispatch for closed types, so a generic handler is registered by closing it at build time: a notification handler declared as H<T> : INotificationHandler<T> over every notification it can see, and a request or stream handler over the types that satisfy its constraints. Anything else is reported here; declare a closed handler per request instead.");

    /// <summary>
    /// A warning, raised only when the other assembly exposes public handlers. A library that uses Çapar
    /// privately keeps its handlers internal and is not reported.
    /// </summary>
    public static readonly DiagnosticDescriptor OwnMediator = new(
        id: "CPR0005",
        title: "Referenced project has its own mediator",
        messageFormat: "'{0}' references Capar and has its own generated mediator, so its handlers ({1}) are not part of this one. If they belong to this application, reference Capar.Contracts from '{0}' instead of Capar.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        customTags: CompilationEnd,
        helpLinkUri: Help + "cpr0005",
        description: "Projects whose handlers belong to the application should reference Capar.Contracts. Only the project that calls AddCapar, usually the host, references Capar itself.");

    /// <summary>
    /// A warning, because the call compiles and does something; it just registers a different project's
    /// handlers, which shows up as "no handler" only when a request is sent. Calling another project's
    /// <c>AddCapar</c> on purpose is possible, which is what a suppression is for.
    /// </summary>
    public static readonly DiagnosticDescriptor ForeignRegistration = new(
        id: "CPR0006",
        title: "AddCapar call registers another project's mediator",
        messageFormat: "This calls the AddCapar generated for '{0}', which registers that project's mediator and handlers, not this project's. To register this project's, call {1}.AddCapar(services).",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: Help + "cpr0006",
        description: "Each project that references Capar gets its own AddCapar. When another project's is visible too, through InternalsVisibleTo, a call can bind to it, and this project's handlers are then never registered.");

    /// <summary>
    /// Info, not a warning: the compiler already reports this code as an error once the MediatR package is
    /// gone. This exists so that one "Fix all" can reach every kind of it, since the IDE scopes a fix-all to
    /// the diagnostic id it was started on and the compiler gives the same code several different ids.
    /// </summary>
    public static readonly DiagnosticDescriptor MediatRUsage = new(
        id: "CPR0007",
        title: "MediatR code to move to Capar",
        messageFormat: "'{0}' is MediatR code; the fix 'Move from MediatR to Capar' rewrites it",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        helpLinkUri: Help + "cpr0007",
        description: "Reported only where MediatR is no longer referenced and the name does not resolve. The code fix rewrites the namespace, the configuration type and the AddMediatR call; 'Fix all in solution' does the whole migration.");

    public static readonly DiagnosticDescriptor[] All = [NoHandler, AmbiguousHandler, Inaccessible, GenericHandler, OwnMediator, ForeignRegistration, MediatRUsage];

    public static DiagnosticDescriptor Descriptor(string rule) => rule switch
    {
        "CPR0001" => NoHandler,
        "CPR0002" => AmbiguousHandler,
        "CPR0003" => Inaccessible,
        "CPR0004" => GenericHandler,
        "CPR0006" => ForeignRegistration,
        "CPR0007" => MediatRUsage,
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
