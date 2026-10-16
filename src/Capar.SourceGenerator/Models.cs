namespace Capar.SourceGenerator;

// Everything in this file is strings, ints, bools and EquatableArrays of the same, and that is the most
// important property these types have. A model holding an ISymbol, a Compilation or a Location keeps the
// whole compilation alive and never compares equal across runs, so the incremental cache silently stops
// working. There is a test that asserts the cache is actually hit.

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
}

/// <summary>One handler contract one class implements. A class handling two messages yields two.</summary>
/// <param name="Kind">The contract.</param>
/// <param name="HandlerType">Fully qualified handler class.</param>
/// <param name="MessageType">Fully qualified request or notification type it handles.</param>
/// <param name="ResponseType">Fully qualified response or stream item type, or null when there is none.</param>
/// <param name="MessageIsValueType">True when the message is a struct, so a null check would box it.</param>
/// <param name="Location">Where the handler is declared, when it is in source.</param>
internal sealed record HandlerModel(
    HandlerKind Kind,
    string HandlerType,
    string MessageType,
    string? ResponseType,
    bool MessageIsValueType,
    LocationModel? Location);

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
/// <param name="MessageType">Fully qualified type.</param>
/// <param name="IsValueType">True for a struct.</param>
/// <param name="AssignableTo">
/// The type itself, every base class and every interface, fully qualified. A notification handler for any of
/// these receives this notification, which is how a handler for a base type or for <c>INotification</c> sees
/// everything derived from it.
/// </param>
/// <param name="FromSource">Declared in this compilation, so a missing handler is worth reporting.</param>
/// <param name="Location">Where it is declared, when it is in source.</param>
internal sealed record MessageModel(
    MessageKind Kind,
    string MessageType,
    bool IsValueType,
    EquatableArray<string> AssignableTo,
    bool FromSource,
    LocationModel? Location);

/// <summary>A type that looked like a handler and was left out, with the reason.</summary>
/// <param name="Rule">The diagnostic to report.</param>
/// <param name="TypeName">The type left out.</param>
/// <param name="Detail">Extra text for the message, such as the assembly it lives in.</param>
/// <param name="Location">Where it is declared, when it is in source.</param>
internal sealed record SkippedModel(
    string Rule,
    string TypeName,
    string Detail,
    LocationModel? Location);

/// <summary>What one declaration or one referenced assembly contributed.</summary>
/// <param name="Handlers">Handler contracts found.</param>
/// <param name="Messages">Concrete messages found.</param>
/// <param name="Skipped">Types left out, with reasons.</param>
internal sealed record DiscoveryModel(
    EquatableArray<HandlerModel> Handlers,
    EquatableArray<MessageModel> Messages,
    EquatableArray<SkippedModel> Skipped)
{
    public bool IsEmpty => Handlers.Length == 0 && Messages.Length == 0 && Skipped.Length == 0;
}

/// <summary>What the referenced assemblies contributed, plus the facts taken from the compilation.</summary>
/// <param name="AssemblyName">Names the generated namespace.</param>
/// <param name="Discovery">Handlers and messages found in references.</param>
internal sealed record ReferenceModel(string AssemblyName, DiscoveryModel Discovery);

/// <summary>A source location reduced to values, because <c>Location</c> holds the syntax tree.</summary>
internal sealed record LocationModel(
    string FilePath,
    int Start,
    int Length,
    int StartLine,
    int StartCharacter,
    int EndLine,
    int EndCharacter);
