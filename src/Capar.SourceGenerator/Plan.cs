using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Capar.SourceGenerator;

/// <summary>A request with exactly one handler, ready to emit.</summary>
internal sealed record RequestEntry(string RequestType, string? ResponseType, string HandlerType, bool IsValueType);

/// <summary>A stream request with exactly one handler, ready to emit.</summary>
internal sealed record StreamEntry(string RequestType, string ResponseType, string HandlerType, bool IsValueType);

/// <summary>One handler a notification reaches, and the notification type that handler was written for.</summary>
/// <param name="HandlerType">The handler class.</param>
/// <param name="HandledType">The type in its <c>INotificationHandler&lt;T&gt;</c>, which may be a base type.</param>
internal sealed record NotificationTarget(string HandlerType, string HandledType);

/// <summary>A concrete notification and every handler it reaches, in the order they run.</summary>
internal sealed record NotificationEntry(string NotificationType, bool IsValueType, EquatableArray<NotificationTarget> Targets);

/// <summary>A diagnostic, reduced to values so it can sit in a cached model.</summary>
internal sealed record DiagnosticEntry(string Rule, EquatableArray<string> Arguments, LocationModel? Location);

/// <summary>Everything the emitter needs, decided.</summary>
internal sealed record EmitPlan(
    string Namespace,
    EquatableArray<RequestEntry> Requests,
    EquatableArray<StreamEntry> Streams,
    EquatableArray<NotificationEntry> Notifications,
    EquatableArray<string> HandlerTypes,
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

        // The same message can arrive twice, from a partial declaration or from source and a reference at
        // once. The source copy wins because it carries a location for diagnostics.
        Dictionary<string, MessageModel> messages = discovery.Messages
            .GroupBy(static message => message.MessageType + "|" + message.Kind, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.OrderByDescending(static message => message.FromSource).First(),
                StringComparer.Ordinal);

        List<RequestEntry> requests = [];
        foreach (IGrouping<string, HandlerModel> group in GroupHandlers(discovery, HandlerKind.Request, HandlerKind.VoidRequest))
        {
            if (Single(group, MessageKind.Request) is { } handler)
            {
                requests.Add(new RequestEntry(handler.MessageType, handler.ResponseType, handler.HandlerType, handler.MessageIsValueType));
            }
        }

        List<StreamEntry> streams = [];
        foreach (IGrouping<string, HandlerModel> group in GroupHandlers(discovery, HandlerKind.Stream))
        {
            if (Single(group, MessageKind.Stream) is { } handler)
            {
                streams.Add(new StreamEntry(handler.MessageType, handler.ResponseType!, handler.HandlerType, handler.MessageIsValueType));
            }
        }

        HashSet<string> handledRequests = new(
            discovery.Handlers
                .Where(static handler => handler.Kind != HandlerKind.Notification)
                .Select(static handler => handler.MessageType),
            StringComparer.Ordinal);

        foreach (MessageModel message in messages.Values
            .Where(static message => message.FromSource && message.Kind != MessageKind.Notification)
            .Where(message => !handledRequests.Contains(message.MessageType))
            .OrderBy(static message => message.MessageType, StringComparer.Ordinal))
        {
            diagnostics.Add(new DiagnosticEntry(
                SourceGenerator.Diagnostics.NoHandler.Id,
                new[] { Trim(message.MessageType) }.ToEquatableArray(),
                message.Location));
        }

        HandlerModel[] notificationHandlers =
        [
            .. discovery.Handlers
                .Where(static handler => handler.Kind == HandlerKind.Notification)
                .OrderBy(static handler => handler.HandlerType, StringComparer.Ordinal)
                .ThenBy(static handler => handler.MessageType, StringComparer.Ordinal),
        ];

        HashSet<string> directlyHandled = new(notificationHandlers.Select(static handler => handler.MessageType), StringComparer.Ordinal);
        List<NotificationEntry> notifications = [];
        foreach (MessageModel message in messages.Values
            .Where(static message => message.Kind == MessageKind.Notification)
            .OrderBy(static message => message.MessageType, StringComparer.Ordinal))
        {
            // MediatR 12 registers a handler for a base type or for INotification only under the notification
            // types that some handler names exactly. A notification nobody handles directly therefore reaches no
            // handler at all, catch-all handlers included. The differential tests caught this, and it is matched
            // on purpose: after migrating, a catch-all audit handler must not start seeing notifications it never
            // saw before.
            if (!directlyHandled.Contains(message.MessageType))
            {
                notifications.Add(new NotificationEntry(message.MessageType, message.IsValueType, EquatableArray<NotificationTarget>.Empty));
                continue;
            }

            HashSet<string> reachable = new(message.AssignableTo, StringComparer.Ordinal);

            // A handler class can implement INotificationHandler<T> for several T that this notification is
            // assignable to. It still runs once, through the most specific of them, which is the first one in
            // AssignableTo order: the type itself, then its base classes, then interfaces.
            NotificationTarget[] targets =
            [
                .. notificationHandlers
                    .Where(handler => reachable.Contains(handler.MessageType))
                    .GroupBy(static handler => handler.HandlerType, StringComparer.Ordinal)
                    .Select(group => group
                        .OrderBy(handler => IndexOf(message.AssignableTo, handler.MessageType))
                        .First())
                    .Select(static handler => new NotificationTarget(handler.HandlerType, handler.MessageType)),
            ];

            notifications.Add(new NotificationEntry(message.MessageType, message.IsValueType, targets.ToEquatableArray()));
        }

        string[] handlerTypes =
        [
            .. requests.Select(static request => request.HandlerType)
                .Concat(streams.Select(static stream => stream.HandlerType))
                .Concat(notificationHandlers.Select(static handler => handler.HandlerType))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static type => type, StringComparer.Ordinal),
        ];

        foreach (SkippedModel skipped in discovery.Skipped.OrderBy(static skipped => skipped.TypeName, StringComparer.Ordinal))
        {
            string[] arguments = skipped.Rule == SourceGenerator.Diagnostics.Inaccessible.Id
                ? [skipped.TypeName, skipped.Detail]
                : [skipped.TypeName];
            diagnostics.Add(new DiagnosticEntry(skipped.Rule, arguments.ToEquatableArray(), skipped.Location));
        }

        return new EmitPlan(
            ToNamespace(assemblyName),
            requests.OrderBy(static request => request.RequestType, StringComparer.Ordinal).ToEquatableArray(),
            streams.OrderBy(static stream => stream.RequestType, StringComparer.Ordinal).ToEquatableArray(),
            notifications.ToEquatableArray(),
            handlerTypes.ToEquatableArray(),
            diagnostics.ToEquatableArray());

        HandlerModel? Single(IGrouping<string, HandlerModel> group, MessageKind kind)
        {
            HandlerModel[] candidates = [.. group.OrderBy(static handler => handler.HandlerType, StringComparer.Ordinal)];
            if (candidates.Length == 1)
            {
                return candidates[0];
            }

            messages.TryGetValue(group.Key + "|" + kind, out MessageModel? message);
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

    private static IEnumerable<IGrouping<string, HandlerModel>> GroupHandlers(DiscoveryModel discovery, params HandlerKind[] kinds) =>
        discovery.Handlers
            .Where(handler => kinds.Contains(handler.Kind))
            .GroupBy(static handler => handler.MessageType, StringComparer.Ordinal)
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

            segments[index] = builder.ToString();
        }

        return string.Join(".", segments);
    }
}
