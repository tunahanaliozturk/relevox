namespace Capar.SourceGenerator;

// Everything in this file is strings, ints, bools and EquatableArrays of the same, and that is the most
// important property these types have. A model holding an ISymbol, a Compilation or a Location keeps the
// whole compilation alive and never compares equal across runs, so the incremental cache silently stops
// working. There is a test that asserts the cache is actually hit.
//
// Type names come in two spellings. The plain one (`global::Ns.Wrapper<string>`) identifies a type and is
// what typeof accepts. The annotated one (`global::Ns.Wrapper<string?>`) keeps nullable annotations and is
// what goes into signatures and casts, so a handler returning `Task<string?>` is not reported to callers as
// returning `Task<string>`.

/// <summary>Which contract a handler implements.</summary>
internal enum HandlerKind
{
    /// <summary><c>IRequestHandler&lt;TRequest, TResponse&gt;</c>.</summary>
    Request,

    /// <summary><c>IRequestHandler&lt;TRequest&gt;</c>.</summary>
    VoidRequest,

    /// <summary><c>INotificationHandler&lt;TNotification&gt;</c>.</summary>
    Notification,

    /// <summary><c>IStreamRequestHandler&lt;TRequest, TResponse&gt;</c>.</summary>
    Stream,

    /// <summary><c>IRequestPreProcessor&lt;TRequest&gt;</c>.</summary>
    PreProcessor,

    /// <summary><c>IRequestPostProcessor&lt;TRequest, TResponse&gt;</c>.</summary>
    PostProcessor,

    /// <summary><c>IRequestExceptionHandler&lt;TRequest, TResponse, TException&gt;</c>.</summary>
    ExceptionHandler,

    /// <summary><c>IRequestExceptionAction&lt;TRequest, TException&gt;</c>.</summary>
    ExceptionAction,
}

/// <summary>Facts about a message type that decide how it can be dispatched.</summary>
/// <param name="Type">Plain fully qualified name, for typeof and identity.</param>
/// <param name="Syntax">Annotated fully qualified name, for signatures and casts.</param>
/// <param name="IsValueType">A struct cannot be null and must not be boxed to check.</param>
/// <param name="IsSealed">
/// A sealed type, or a struct. Anything else can arrive as a derived type at run time, and a typed overload
/// has to check before it trusts the static type.
/// </param>
/// <param name="IsAbstract">An abstract class or an interface, which can never be the runtime type.</param>
internal sealed record MessageShape(string Type, string Syntax, bool IsValueType, bool IsSealed, bool IsAbstract);

/// <summary>One handler contract one class implements. A class handling two messages yields two.</summary>
/// <param name="Kind">The contract.</param>
/// <param name="HandlerType">Fully qualified handler class.</param>
/// <param name="Message">The request or notification type it handles.</param>
/// <param name="ResponseType">Plain response or stream item type, for typeof, or null when there is none.</param>
/// <param name="ResponseSyntax">Annotated response or stream item type, or null when there is none.</param>
/// <param name="ExceptionChain">
/// For exception handlers and actions: the exception type they name, then its base types, plain names.
/// Empty for everything else.
/// </param>
/// <param name="IsPublic">Visible outside its own assembly, which matters for CPR0005.</param>
/// <param name="ResponseChain">
/// For post-processors: the response type, its base types and its interfaces, plain names. Empty otherwise.
/// </param>
/// <param name="Order">
/// Where the handler is declared. MediatR registers what its scan finds in metadata order, and that
/// registration order is the order notification handlers, processors and exception handlers run in.
/// </param>
/// <param name="Location">Where the handler is declared, when it is in source.</param>
internal sealed record HandlerModel(
    HandlerKind Kind,
    string HandlerType,
    MessageShape Message,
    string? ResponseType,
    string? ResponseSyntax,
    EquatableArray<string> ExceptionChain,
    EquatableArray<string> ResponseChain,
    bool IsPublic,
    DeclarationOrder Order,
    LocationModel? Location);

/// <summary>Where a type is declared, as the parts of a sort key that reproduces metadata order.</summary>
/// <param name="Assembly">0 for this compilation; for a reference, one more than its position among the references.</param>
/// <param name="Namespace">For source, the containing namespace, empty for the global one.</param>
/// <param name="Tree">For source, the syntax tree's index in the compilation.</param>
/// <param name="Position">For source, the declaration's start in its tree; for metadata, its TypeDef token.</param>
/// <remarks>
/// Metadata order for a referenced assembly is simply its TypeDef table, which the token gives. For this
/// compilation it has to be reconstructed: the compiler emits a namespace's own types, in declaration order,
/// before the types of its child namespaces. The differential tests established that with a handler in a
/// nested namespace declared in the first file, which still runs last. The planner does the reconstruction,
/// because it needs every handler's namespace to order sibling namespaces.
/// </remarks>
internal sealed record DeclarationOrder(int Assembly, string Namespace, int Tree, int Position);

/// <summary>Which contract a message implements.</summary>
internal enum MessageKind
{
    /// <summary><c>IRequest&lt;TResponse&gt;</c> or <c>IRequest</c>.</summary>
    Request,

    /// <summary><c>INotification</c>.</summary>
    Notification,

    /// <summary><c>IStreamRequest&lt;TResponse&gt;</c>.</summary>
    Stream,
}

/// <summary>A concrete message type: something that can actually be sent or published.</summary>
/// <param name="Kind">What kind of message.</param>
/// <param name="Shape">Its name and dispatch facts.</param>
/// <param name="AssignableTo">
/// The type itself, every base class and every interface, plain names. A notification handler for any of
/// these receives this notification, which is how a handler for a base type sees what derives from it. For
/// a struct it is the struct alone: variance does not apply to value types, so the container would not hand
/// an <c>INotificationHandler&lt;INotification&gt;</c> to a struct notification either.
/// </param>
/// <param name="FromSource">Declared in this compilation, so a missing handler is worth reporting.</param>
/// <param name="Location">Where it is declared, when it is in source.</param>
internal sealed record MessageModel(
    MessageKind Kind,
    MessageShape Shape,
    EquatableArray<string> AssignableTo,
    bool FromSource,
    LocationModel? Location);

/// <summary>A type that looked like a handler and was left out, with the reason.</summary>
/// <param name="Rule">The diagnostic to report.</param>
/// <param name="Arguments">The message arguments.</param>
/// <param name="Location">Where it is declared, when it is in source.</param>
internal sealed record SkippedModel(
    string Rule,
    EquatableArray<string> Arguments,
    LocationModel? Location);

/// <summary>
/// An open generic registration, such as a <c>LoggingPreProcessor&lt;T&gt;</c> that applies to every request.
/// </summary>
/// <param name="ServiceType">The unbound service type, such as <c>global::Capar.Pipeline.IRequestPreProcessor&lt;&gt;</c>.</param>
/// <param name="ImplementationType">The unbound implementation type.</param>
internal sealed record OpenRegistration(string ServiceType, string ImplementationType);

/// <summary>What one declaration or one referenced assembly contributed.</summary>
/// <param name="Handlers">Handler contracts found.</param>
/// <param name="Messages">Concrete messages found.</param>
/// <param name="Skipped">Types left out, with reasons.</param>
/// <param name="Open">Open generic processors, registered as they are.</param>
/// <param name="Experimental">
/// Diagnostic ids from <c>[Experimental]</c> on any type the generated code names. The generated files
/// suppress exactly these, so using an experimental handler does not break a build that already opted in.
/// </param>
internal sealed record DiscoveryModel(
    EquatableArray<HandlerModel> Handlers,
    EquatableArray<MessageModel> Messages,
    EquatableArray<SkippedModel> Skipped,
    EquatableArray<OpenRegistration> Open,
    EquatableArray<string> Experimental)
{
    public bool IsEmpty => Handlers.Length == 0 && Messages.Length == 0 && Skipped.Length == 0 && Open.Length == 0 && Experimental.Length == 0;
}

/// <summary>What the referenced assemblies contributed, plus the facts taken from the compilation.</summary>
/// <param name="AssemblyName">Names the generated namespace.</param>
/// <param name="Discovery">Handlers and messages found in references.</param>
/// <param name="SeesAnotherRegistration">Whether a reference's generated <c>AddCapar</c> is visible here.</param>
internal sealed record ReferenceModel(string AssemblyName, DiscoveryModel Discovery, bool SeesAnotherRegistration);

/// <summary>A source location reduced to values, because <c>Location</c> holds the syntax tree.</summary>
internal sealed record LocationModel(
    string FilePath,
    int Start,
    int Length,
    int StartLine,
    int StartCharacter,
    int EndLine,
    int EndCharacter);
