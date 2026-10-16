using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis.CSharp;

namespace Capar.SourceGenerator;

/// <summary>A request with exactly one handler, ready to emit.</summary>
/// <param name="Request">The request type.</param>
/// <param name="ResponseType">Plain response type, for typeof, or null for a request that returns nothing.</param>
/// <param name="ResponseSyntax">Annotated response type, or null for a request that returns nothing.</param>
/// <param name="HandlerType">The handler class.</param>
internal sealed record RequestEntry(MessageShape Request, string? ResponseType, string? ResponseSyntax, string HandlerType);

/// <summary>A closed service registration, such as a pre-processor under its <c>IRequestPreProcessor&lt;T&gt;</c>.</summary>
internal sealed record ServiceRegistration(string ServiceType, string ImplementationType);

/// <summary>A stream request with exactly one handler, ready to emit.</summary>
internal sealed record StreamEntry(MessageShape Request, string ResponseSyntax, string HandlerType);

/// <summary>One handler a notification reaches, and the notification type that handler was written for.</summary>
/// <param name="HandlerType">The handler class.</param>
/// <param name="HandledSyntax">The annotated type in its <c>INotificationHandler&lt;T&gt;</c>, which may be a base type.</param>
internal sealed record NotificationTarget(string HandlerType, string HandledSyntax);

/// <summary>A concrete notification and every handler it reaches, in the order they run.</summary>
internal sealed record NotificationEntry(MessageShape Notification, EquatableArray<NotificationTarget> Targets);

/// <summary>A diagnostic, reduced to values so it can sit in a cached model.</summary>
internal sealed record DiagnosticEntry(string Rule, EquatableArray<string> Arguments, LocationModel? Location);

/// <summary>Everything the emitter needs, decided.</summary>
internal sealed record EmitPlan(
    string Namespace,
    EquatableArray<RequestEntry> Requests,
    EquatableArray<StreamEntry> Streams,
    EquatableArray<NotificationEntry> Notifications,
    EquatableArray<string> HandlerTypes,
    EquatableArray<ServiceRegistration> Processors,
    EquatableArray<OpenRegistration> OpenProcessors,
    EquatableArray<string> SuppressedWarnings,
    EquatableArray<DiagnosticEntry> Diagnostics);

/// <summary>Turns what discovery found into what gets emitted, deciding every conflict on the way.</summary>
/// <remarks>
/// Pure and deterministic: the same discovery produces the same plan, byte for byte, whatever order the
/// compiler visited files in. Every list is sorted with an ordinal comparison before it leaves here.
/// </remarks>
internal static class Planner
{
    public static EmitPlan Build(string assemblyName, DiscoveryModel discovery)
    {
        List<DiagnosticEntry> diagnostics = [];

        // The same message can arrive more than once: from a partial declaration, from a notification handler
        // naming a closed generic, or from source and a reference at once. The source copy wins because it
        // carries a location for diagnostics.
        Dictionary<string, MessageModel> messages = discovery.Messages
            .GroupBy(static message => Key(message.Shape.Type, message.Kind), StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.OrderByDescending(static message => message.FromSource).First(),
                StringComparer.Ordinal);

        List<RequestEntry> requests = [];
        foreach (IGrouping<string, HandlerModel> group in GroupHandlers(discovery, HandlerKind.Request, HandlerKind.VoidRequest))
        {
            if (Single(group, MessageKind.Request) is { } handler)
            {
                requests.Add(new RequestEntry(handler.Message, handler.ResponseType, handler.ResponseSyntax, handler.HandlerType));
            }
        }

        List<StreamEntry> streams = [];
        foreach (IGrouping<string, HandlerModel> group in GroupHandlers(discovery, HandlerKind.Stream))
        {
            if (Single(group, MessageKind.Stream) is { } handler)
            {
                streams.Add(new StreamEntry(handler.Message, handler.ResponseSyntax!, handler.HandlerType));
            }
        }

        HashSet<string> handledRequests = new(
            discovery.Handlers
                .Where(static handler => handler.Kind is HandlerKind.Request or HandlerKind.VoidRequest or HandlerKind.Stream)
                .Select(static handler => handler.Message.Type),
            StringComparer.Ordinal);

        foreach (MessageModel message in messages.Values
            .Where(static message => message.FromSource && message.Kind != MessageKind.Notification)
            .Where(message => !handledRequests.Contains(message.Shape.Type))
            .OrderBy(static message => message.Shape.Type, StringComparer.Ordinal))
        {
            diagnostics.Add(new DiagnosticEntry(
                SourceGenerator.Diagnostics.NoHandler.Id,
                new[] { Trim(message.Shape.Type) }.ToEquatableArray(),
                message.Location));
        }

        HandlerModel[] notificationHandlers =
        [
            .. discovery.Handlers
                .Where(static handler => handler.Kind == HandlerKind.Notification)
                .OrderBy(static handler => handler.HandlerType, StringComparer.Ordinal)
                .ThenBy(static handler => handler.Message.Type, StringComparer.Ordinal),
        ];

        HashSet<string> directlyHandled = new(notificationHandlers.Select(static handler => handler.Message.Type), StringComparer.Ordinal);
        List<NotificationEntry> notifications = [];
        foreach (MessageModel message in messages.Values
            .Where(static message => message.Kind == MessageKind.Notification)
            .OrderBy(static message => message.Shape.Type, StringComparer.Ordinal))
        {
            // MediatR 12 registers a handler for a base type or for INotification only under the notification
            // types that some handler names exactly. A notification nobody handles directly therefore reaches no
            // handler at all, catch-all handlers included. The differential tests caught this, and it is matched
            // on purpose: after migrating, a catch-all audit handler must not start seeing notifications it never
            // saw before.
            if (!directlyHandled.Contains(message.Shape.Type))
            {
                notifications.Add(new NotificationEntry(message.Shape, EquatableArray<NotificationTarget>.Empty));
                continue;
            }

            HashSet<string> reachable = new(message.AssignableTo, StringComparer.Ordinal);

            // A handler class can implement INotificationHandler<T> for several T that this notification is
            // assignable to. It still runs once, through the most specific of them, which is the first one in
            // AssignableTo order: the type itself, then its base classes, then interfaces.
            NotificationTarget[] targets =
            [
                .. notificationHandlers
                    .Where(handler => reachable.Contains(handler.Message.Type))
                    .GroupBy(static handler => handler.HandlerType, StringComparer.Ordinal)
                    .Select(group => group
                        .OrderBy(handler => IndexOf(message.AssignableTo, handler.Message.Type))
                        .First())
                    .Select(static handler => new NotificationTarget(handler.HandlerType, handler.Message.Syntax)),
            ];

            notifications.Add(new NotificationEntry(message.Shape, targets.ToEquatableArray()));
        }

        string[] handlerTypes =
        [
            .. requests.Select(static request => request.HandlerType)
                .Concat(streams.Select(static stream => stream.HandlerType))
                .Concat(notificationHandlers.Select(static handler => handler.HandlerType))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static type => type, StringComparer.Ordinal),
        ];

        // Processors are registered under the interface they implement, as MediatR's scan does. They are not
        // dispatched to directly: the built-in pre and post processor behaviours resolve them per request.
        //
        // As with notifications, a processor written for a base type or an interface is also registered under
        // every request type that some processor names exactly and that derives from it. A request no processor
        // names exactly gets none of the base-type ones. The differential tests established both halves.
        HandlerModel[] processorHandlers =
        [
            .. discovery.Handlers.Where(static handler => handler.Kind is HandlerKind.PreProcessor or HandlerKind.PostProcessor),
        ];

        List<ServiceRegistration> processors = [];
        foreach (HandlerModel processor in processorHandlers)
        {
            processors.Add(new ServiceRegistration(ProcessorService(processor, processor.Message.Type), processor.HandlerType));
        }

        foreach (string named in processorHandlers.Select(static processor => processor.Message.Type).Distinct(StringComparer.Ordinal))
        {
            if (!messages.TryGetValue(Key(named, MessageKind.Request), out MessageModel? request))
            {
                continue;
            }

            HashSet<string> bases = new(request.AssignableTo.Where(type => type != named), StringComparer.Ordinal);
            foreach (HandlerModel processor in processorHandlers.Where(processor => bases.Contains(processor.Message.Type)))
            {
                bool namesThisKind = processorHandlers.Any(other => other.Kind == processor.Kind && other.Message.Type == named);
                if (namesThisKind)
                {
                    processors.Add(new ServiceRegistration(ProcessorService(processor, named), processor.HandlerType));
                }
            }
        }

        processors = [.. processors.Distinct()
            .OrderBy(static registration => registration.ServiceType, StringComparer.Ordinal)
            .ThenBy(static registration => registration.ImplementationType, StringComparer.Ordinal)];

        foreach (SkippedModel skipped in discovery.Skipped
            .OrderBy(static skipped => skipped.Rule, StringComparer.Ordinal)
            .ThenBy(static skipped => string.Join("|", skipped.Arguments), StringComparer.Ordinal))
        {
            diagnostics.Add(new DiagnosticEntry(skipped.Rule, skipped.Arguments, skipped.Location));
        }

        return new EmitPlan(
            ToNamespace(assemblyName),
            requests.OrderBy(static request => request.Request.Type, StringComparer.Ordinal).ToEquatableArray(),
            streams.OrderBy(static stream => stream.Request.Type, StringComparer.Ordinal).ToEquatableArray(),
            notifications.ToEquatableArray(),
            handlerTypes.ToEquatableArray(),
            processors.ToEquatableArray(),
            discovery.Open
                .OrderBy(static registration => registration.ServiceType, StringComparer.Ordinal)
                .ThenBy(static registration => registration.ImplementationType, StringComparer.Ordinal)
                .ToEquatableArray(),
            discovery.Experimental,
            diagnostics.ToEquatableArray());

        HandlerModel? Single(IGrouping<string, HandlerModel> group, MessageKind kind)
        {
            HandlerModel[] candidates = [.. group.OrderBy(static handler => handler.HandlerType, StringComparer.Ordinal)];
            if (candidates.Length == 1)
            {
                return candidates[0];
            }

            messages.TryGetValue(Key(group.Key, kind), out MessageModel? message);
            diagnostics.Add(new DiagnosticEntry(
                SourceGenerator.Diagnostics.AmbiguousHandler.Id,
                new[]
                {
                    Trim(group.Key),
                    candidates.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    string.Join(", ", candidates.Select(static handler => Trim(handler.HandlerType))),
                }.ToEquatableArray(),
                message?.Location ?? candidates.Select(static handler => handler.Location).FirstOrDefault(static location => location is not null)));
            return null;
        }
    }

    private static string Key(string type, MessageKind kind) => type + "|" + kind;

    private static string ProcessorService(HandlerModel processor, string requestType) =>
        processor.Kind == HandlerKind.PreProcessor
            ? $"global::Capar.Pipeline.IRequestPreProcessor<{requestType}>"
            : $"global::Capar.Pipeline.IRequestPostProcessor<{requestType}, {processor.ResponseType}>";

    private static IEnumerable<IGrouping<string, HandlerModel>> GroupHandlers(DiscoveryModel discovery, params HandlerKind[] kinds) =>
        discovery.Handlers
            .Where(handler => kinds.Contains(handler.Kind))
            .GroupBy(static handler => handler.Message.Type, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal);

    private static int IndexOf(EquatableArray<string> names, string name)
    {
        for (int index = 0; index < names.Length; index++)
        {
            if (string.Equals(names[index], name, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return int.MaxValue;
    }

    public static string Trim(string fullyQualified) =>
        fullyQualified.StartsWith("global::", StringComparison.Ordinal)
            ? fullyQualified.Substring("global::".Length)
            : fullyQualified;

    /// <summary>
    /// The assembly name, made into a valid namespace.
    /// </summary>
    /// <remarks>
    /// The generated types are internal and live in a namespace named after their assembly, so two projects
    /// that both reference Çapar, one with InternalsVisibleTo the other, never declare the same type name.
    /// A shared fixed namespace would give CS0436 in every test project that can see the app's internals.
    /// </remarks>
    public static string ToNamespace(string assemblyName)
    {
        string[] segments = assemblyName.Split('.');
        for (int index = 0; index < segments.Length; index++)
        {
            StringBuilder builder = new();
            foreach (char character in segments[index])
            {
                builder.Append(char.IsLetterOrDigit(character) || character == '_' ? character : '_');
            }

            if (builder.Length == 0 || char.IsDigit(builder[0]))
            {
                builder.Insert(0, '_');
            }

            string segment = builder.ToString();
            segments[index] = SyntaxFacts.GetKeywordKind(segment) != SyntaxKind.None ? "@" + segment : segment;
        }

        return string.Join(".", segments);
    }
}
