using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis.CSharp;

namespace Relevox.SourceGenerator;

/// <summary>A request with exactly one handler, ready to emit.</summary>
/// <param name="Request">The request type.</param>
/// <param name="ResponseType">Plain response type, for typeof, or null for a request that returns nothing.</param>
/// <param name="ResponseSyntax">Annotated response type, or null for a request that returns nothing.</param>
/// <param name="HandlerType">The handler class.</param>
internal sealed record RequestEntry(MessageShape Request, string? ResponseType, string? ResponseSyntax, string HandlerType);

/// <summary>A closed service registration, such as a pre-processor under its <c>IRequestPreProcessor&lt;T&gt;</c>.</summary>
internal sealed record ServiceRegistration(string ServiceType, string ImplementationType);

/// <summary>
/// The exception types one request has handlers or actions for, which its generated catalog walks.
/// </summary>
/// <param name="Request">The request type.</param>
/// <param name="ResponseType">Plain response type; <c>global::Relevox.Unit</c> for a request without one.</param>
/// <param name="ResponseSyntax">The same, with nullable annotations, for the generated class.</param>
/// <param name="HandledExceptions">Exception types with handlers, plain names.</param>
/// <param name="ActionExceptions">Exception types with actions, plain names.</param>
internal sealed record ExceptionCatalogEntry(
    MessageShape Request,
    string ResponseType,
    string ResponseSyntax,
    EquatableArray<string> HandledExceptions,
    EquatableArray<string> ActionExceptions);

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
/// <param name="Namespace">Where the generated mediator lives, named after the assembly.</param>
/// <param name="RegistrationNamespace">Where <c>AddRelevox</c> lives; see <see cref="Planner.RegistrationNamespaceFor"/>.</param>
/// <param name="OpenNotificationHandlers">
/// Generic notification handlers, unbound and in the order they run, for notification types the build never saw.
/// </param>
/// <param name="HandlerContracts">
/// Each handler under the interface it implements, as MediatR registers it, for code that injects the interface.
/// Annotated names: a typeof with the plain name breaks the request's nullable constraint.
/// </param>
/// <param name="Emits">False for a project that only uses another project's mediator; see <see cref="Planner.Build"/>.</param>
internal sealed record EmitPlan(
    string Namespace,
    string RegistrationNamespace,
    EquatableArray<RequestEntry> Requests,
    EquatableArray<StreamEntry> Streams,
    EquatableArray<NotificationEntry> Notifications,
    EquatableArray<string> HandlerTypes,
    EquatableArray<ServiceRegistration> Processors,
    EquatableArray<OpenRegistration> OpenProcessors,
    EquatableArray<ServiceRegistration> ExceptionRegistrations,
    EquatableArray<ExceptionCatalogEntry> ExceptionCatalogs,
    EquatableArray<string> SuppressedWarnings,
    EquatableArray<DiagnosticEntry> Diagnostics,
    EquatableArray<string> OpenNotificationHandlers,
    EquatableArray<ServiceRegistration> HandlerContracts,
    bool Emits = true);

/// <summary>Turns what discovery found into what gets emitted, deciding every conflict on the way.</summary>
/// <remarks>
/// Pure and deterministic: the same discovery produces the same plan, byte for byte, whatever order the
/// compiler visited files in. Every list is sorted with an ordinal comparison before it leaves here.
/// </remarks>
internal static class Planner
{
    /// <remarks>
    /// A project with no handlers or processors of its own, that does not call <c>AddRelevox</c>, and whose
    /// references put every handler it can see under a referenced project's mediator, is a consumer of that
    /// mediator: an API or a test project downstream of the application layer that calls <c>AddRelevox</c>.
    /// Analyzers flow to it through the project reference, so the generator runs there too; it writes nothing
    /// and does not report the upstream project's handlers as RVX0005. A real migration found this: every
    /// project below the application layer warned, and with warnings as errors, failed.
    /// </remarks>
    public static EmitPlan Build(
        string assemblyName,
        DiscoveryModel discovery,
        bool seesAnotherRegistration = false,
        bool mediatorCoversReferences = false,
        bool callsAddRelevox = false,
        bool ownOpenProcessors = false)
    {
        bool consumer = mediatorCoversReferences
            && !callsAddRelevox
            && !ownOpenProcessors
            && discovery.Handlers.All(static handler => handler.Order.Assembly != 0)
            && discovery.OpenNotifications.All(static open => open.Order.Assembly != 0)
            && !discovery.Skipped.Any(static skipped => skipped.Location is not null);
        List<DiagnosticEntry> diagnostics = [];
        MetadataOrder order = new(discovery.Handlers.Select(static handler => handler.Order)
            .Concat(discovery.OpenNotifications.Select(static open => open.Order)));

        // The same message can arrive more than once: from a partial declaration, from a notification handler
        // naming a closed generic, or from source and a reference at once. The source copy wins because it
        // carries a location for diagnostics.
        Dictionary<string, MessageModel> messages = discovery.Messages
            .GroupBy(static message => Key(message.Shape.Type, message.Kind), StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.OrderByDescending(static message => message.FromSource).First(),
                StringComparer.Ordinal);

        List<ServiceRegistration> contracts = [];
        List<RequestEntry> requests = [];
        foreach (IGrouping<string, HandlerModel> group in GroupHandlers(discovery, HandlerKind.Request, HandlerKind.VoidRequest))
        {
            if (Single(group, MessageKind.Request) is { } handler)
            {
                requests.Add(new RequestEntry(handler.Message, handler.ResponseType, handler.ResponseSyntax, handler.HandlerType));
                contracts.Add(new ServiceRegistration(
                    handler.ResponseType is null
                        ? $"global::Relevox.IRequestHandler<{handler.Message.Syntax}>"
                        : $"global::Relevox.IRequestHandler<{handler.Message.Syntax}, {handler.ResponseSyntax}>",
                    handler.HandlerType));
            }
        }

        List<StreamEntry> streams = [];
        foreach (IGrouping<string, HandlerModel> group in GroupHandlers(discovery, HandlerKind.Stream))
        {
            if (Single(group, MessageKind.Stream) is { } handler)
            {
                streams.Add(new StreamEntry(handler.Message, handler.ResponseSyntax!, handler.HandlerType));
                contracts.Add(new ServiceRegistration(
                    $"global::Relevox.IStreamRequestHandler<{handler.Message.Syntax}, {handler.ResponseSyntax}>",
                    handler.HandlerType));
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

        // Handlers closed from a generic definition come after everything else, as MediatR registers open generic
        // handlers after the ones its scan closed.
        HandlerModel[] notificationHandlers =
        [
            .. discovery.Handlers
                .Where(static handler => handler.Kind == HandlerKind.Notification)
                .OrderBy(static handler => handler.IsClosure)
                .ThenBy(static handler => handler.Order, order)
                .ThenBy(static handler => handler.HandlerType, StringComparer.Ordinal)
                .ThenBy(static handler => handler.Message.Type, StringComparer.Ordinal),
        ];

        // A closing does not count as naming the notification: MediatR's scan works out the catch-all
        // registrations before the container ever closes an open generic handler.
        HashSet<string> directlyHandled = new(
            notificationHandlers.Where(static handler => !handler.IsClosure).Select(static handler => handler.Message.Type),
            StringComparer.Ordinal);
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
            // The container closes an open generic handler for exactly the type being published, so a closing
            // reaches its own notification whether or not anything else handles it.
            NotificationTarget[] closings =
            [
                .. notificationHandlers
                    .Where(handler => handler.IsClosure && handler.Message.Type == message.Shape.Type)
                    .Select(static handler => new NotificationTarget(handler.HandlerType, handler.Message.Syntax)),
            ];

            if (!directlyHandled.Contains(message.Shape.Type))
            {
                notifications.Add(new NotificationEntry(message.Shape, closings.ToEquatableArray()));
                continue;
            }

            HashSet<string> reachable = new(message.AssignableTo, StringComparer.Ordinal);

            // A handler class can implement INotificationHandler<T> for several T that this notification is
            // assignable to. It still runs once, through the most specific of them, which is the first one in
            // AssignableTo order: the type itself, then its base classes, then interfaces.
            NotificationTarget[] targets =
            [
                .. notificationHandlers
                    .Where(handler => !handler.IsClosure && reachable.Contains(handler.Message.Type))
                    .GroupBy(static handler => handler.HandlerType, StringComparer.Ordinal)
                    .Select(group => group
                        .OrderBy(handler => IndexOf(message.AssignableTo, handler.Message.Type))
                        .First())
                    .Select(static handler => new NotificationTarget(handler.HandlerType, handler.Message.Syntax)),
                .. closings,
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
            .. discovery.Handlers
                .Where(static handler => handler.Kind is HandlerKind.PreProcessor or HandlerKind.PostProcessor)
                .OrderBy(static handler => handler.Order, order)
                .ThenBy(static handler => handler.HandlerType, StringComparer.Ordinal),
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
                // A post-processor is contravariant in the response as well: one for object reaches a request
                // whose named closing has a string response, registered under that closing.
                foreach (HandlerModel naming in processorHandlers.Where(other => other.Kind == processor.Kind && other.Message.Type == named))
                {
                    if (processor.Kind == HandlerKind.PreProcessor)
                    {
                        processors.Add(new ServiceRegistration(ProcessorService(processor, named), processor.HandlerType));
                    }
                    else if (naming.ResponseChain.Contains(processor.ResponseType!))
                    {
                        processors.Add(new ServiceRegistration(ProcessorService(naming, named), processor.HandlerType));
                    }
                }
            }
        }

        // Registration order within one service is the order processors run in, so it follows declaration order;
        // the order across services does not matter.
        Dictionary<string, int> declared = processorHandlers
            .Select(static (handler, index) => (handler.HandlerType, index))
            .GroupBy(static pair => pair.HandlerType, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Min(static pair => pair.index), StringComparer.Ordinal);
        processors = [.. processors.Distinct()
            .OrderBy(static registration => registration.ServiceType, StringComparer.Ordinal)
            .ThenBy(registration => declared[registration.ImplementationType])];

        (ServiceRegistration[] exceptionRegistrations, ExceptionCatalogEntry[] catalogs) = PlanExceptions(discovery, messages, requests, order);

        foreach (SkippedModel skipped in discovery.Skipped
            // A consumer says nothing about its references; what it says about its own source still stands.
            .Where(skipped => !(consumer && skipped.Location is null))
            .OrderBy(static skipped => skipped.Rule, StringComparer.Ordinal)
            .ThenBy(static skipped => string.Join("|", skipped.Arguments), StringComparer.Ordinal))
        {
            diagnostics.Add(new DiagnosticEntry(skipped.Rule, skipped.Arguments, skipped.Location));
        }

        return new EmitPlan(
            ToNamespace(assemblyName),
            RegistrationNamespaceFor(assemblyName, seesAnotherRegistration),
            requests.OrderBy(static request => request.Request.Type, StringComparer.Ordinal).ToEquatableArray(),
            streams.OrderBy(static stream => stream.Request.Type, StringComparer.Ordinal).ToEquatableArray(),
            notifications.ToEquatableArray(),
            handlerTypes.ToEquatableArray(),
            processors.ToEquatableArray(),
            discovery.Open
                .OrderBy(static registration => registration.ServiceType, StringComparer.Ordinal)
                .ThenBy(static registration => registration.ImplementationType, StringComparer.Ordinal)
                .ToEquatableArray(),
            exceptionRegistrations.ToEquatableArray(),
            catalogs.ToEquatableArray(),
            discovery.Experimental,
            diagnostics.ToEquatableArray(),
            discovery.OpenNotifications
                .OrderBy(static open => open.Order, order)
                .ThenBy(static open => open.Definition, StringComparer.Ordinal)
                .Select(static open => open.Definition)
                .Distinct(StringComparer.Ordinal)
                .ToEquatableArray(),
            // MediatR registers a base-type handler under each notification it reaches as well, in the order they
            // run, so a notification's own list comes first and a handler's declared interface after it.
            contracts
                .Concat(notifications.SelectMany(static notification => notification.Targets.Select(target => new ServiceRegistration(
                    $"global::Relevox.INotificationHandler<{notification.Notification.Syntax}>",
                    target.HandlerType))))
                .Concat(notificationHandlers.Select(static handler => new ServiceRegistration(
                    $"global::Relevox.INotificationHandler<{handler.Message.Syntax}>",
                    handler.HandlerType)))
                .Distinct()
                .ToEquatableArray(),
            Emits: !consumer);

        HandlerModel? Single(IGrouping<string, HandlerModel> group, MessageKind kind)
        {
            HandlerModel[] candidates = [.. group.OrderBy(static handler => handler.HandlerType, StringComparer.Ordinal)];

            // A handler written for this exact request wins over one closed from a generic handler. MediatR with
            // RegisterGenericHandlers registers both and the container returns the last; the written one is the
            // one somebody meant.
            if (candidates.Any(static handler => !handler.IsClosure))
            {
                candidates = [.. candidates.Where(static handler => !handler.IsClosure)];
            }

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

    /// <summary>
    /// Registrations and per-request catalogs for exception handlers and actions.
    /// </summary>
    /// <remarks>
    /// The rule matches MediatR's scan, established against it: each handler is registered under the closed
    /// interface it implements, and also under every closing that some other handler names exactly, when the
    /// request and exception there derive from its own. A handler for <c>Exception</c> is therefore reached
    /// through <c>InvalidOperationException</c> as soon as any handler names that, and runs at that step.
    /// </remarks>
    private static (ServiceRegistration[] Registrations, ExceptionCatalogEntry[] Catalogs) PlanExceptions(
        DiscoveryModel discovery,
        Dictionary<string, MessageModel> messages,
        List<RequestEntry> requests,
        MetadataOrder order)
    {
        HandlerModel[] handlers =
        [
            .. discovery.Handlers
                .Where(static handler => handler.Kind == HandlerKind.ExceptionHandler)
                .OrderBy(static handler => handler.Order, order)
                .ThenBy(static handler => handler.HandlerType, StringComparer.Ordinal),
        ];
        HandlerModel[] actions =
        [
            .. discovery.Handlers
                .Where(static handler => handler.Kind == HandlerKind.ExceptionAction)
                .OrderBy(static handler => handler.Order, order)
                .ThenBy(static handler => handler.HandlerType, StringComparer.Ordinal),
        ];
        Dictionary<string, int> declared = handlers.Concat(actions)
            .Select(static (handler, index) => (handler.HandlerType, index))
            .GroupBy(static pair => pair.HandlerType, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Min(static pair => pair.index), StringComparer.Ordinal);

        // (request, response, exception) for handlers; response is null for actions. Extra marks a registration
        // reached through a base type rather than named by the handler itself.
        List<(string Request, string? Response, string Exception, string Implementation, bool Extra)> closings = [];
        foreach (HandlerModel[] group in new[] { handlers, actions })
        {
            foreach (HandlerModel handler in group)
            {
                closings.Add((handler.Message.Type, handler.ResponseType, handler.ExceptionChain[0], handler.HandlerType, false));
            }

            var named = group
                .Select(static handler => (Request: handler.Message.Type, Response: handler.ResponseType, Exception: handler.ExceptionChain[0], Chain: handler.ExceptionChain))
                .Distinct()
                .ToArray();

            foreach (var closing in named)
            {
                if (!messages.TryGetValue(Key(closing.Request, MessageKind.Request), out MessageModel? request))
                {
                    continue;
                }

                foreach (HandlerModel candidate in group)
                {
                    bool sameShape = candidate.Message.Type == closing.Request && candidate.ExceptionChain[0] == closing.Exception;
                    if (!sameShape
                        && candidate.ResponseType == closing.Response
                        && request.AssignableTo.Contains(candidate.Message.Type)
                        && closing.Chain.Contains(candidate.ExceptionChain[0]))
                    {
                        closings.Add((closing.Request, closing.Response, closing.Exception, candidate.HandlerType, true));
                    }
                }
            }
        }

        // Within one closing the container returns registrations in order, and that is the order handlers run
        // in. MediatR's scan registers in declaration order, so that is the order here too, whether a handler
        // names the closing itself or reaches it through a base type.
        ServiceRegistration[] registrations =
        [
            .. closings
                .OrderBy(closing => declared[closing.Implementation])
                .Select(static closing => new ServiceRegistration(
                    closing.Response is null
                        ? $"global::Relevox.Pipeline.IRequestExceptionAction<{closing.Request}, {closing.Exception}>"
                        : $"global::Relevox.Pipeline.IRequestExceptionHandler<{closing.Request}, {closing.Response}, {closing.Exception}>",
                    closing.Implementation))
                .Distinct(),
        ];

        // Open generic handlers and actions are closed by the container, but only over exception types the
        // catalog asks about. Adding Exception for every request when one exists means they at least run.
        bool openHandlers = discovery.Open.Any(static open => open.ServiceType.EndsWith("IRequestExceptionHandler<,,>", StringComparison.Ordinal));
        bool openActions = discovery.Open.Any(static open => open.ServiceType.EndsWith("IRequestExceptionAction<,>", StringComparison.Ordinal));
        const string AnyException = "global::System.Exception";

        List<ExceptionCatalogEntry> catalogs = [];
        foreach (RequestEntry request in requests.Where(static request => !request.Request.IsAbstract).OrderBy(static request => request.Request.Type, StringComparer.Ordinal))
        {
            string response = request.ResponseType ?? "global::Relevox.Unit";
            string[] handled =
            [
                .. closings
                    .Where(closing => closing.Request == request.Request.Type && closing.Response == response)
                    .Select(static closing => closing.Exception)
                    .Concat(openHandlers ? [AnyException] : [])
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static type => type, StringComparer.Ordinal),
            ];
            string[] acted =
            [
                .. closings
                    .Where(closing => closing.Request == request.Request.Type && closing.Response is null)
                    .Select(static closing => closing.Exception)
                    .Concat(openActions ? [AnyException] : [])
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static type => type, StringComparer.Ordinal),
            ];

            if (handled.Length > 0 || acted.Length > 0)
            {
                catalogs.Add(new ExceptionCatalogEntry(request.Request, response, request.ResponseSyntax ?? "global::Relevox.Unit", handled.ToEquatableArray(), acted.ToEquatableArray()));
            }
        }

        return (registrations, [.. catalogs]);
    }

    /// <summary>
    /// Orders handlers the way they appear in metadata, which is the order MediatR's scan registers them in.
    /// </summary>
    /// <remarks>
    /// A referenced assembly's order is its TypeDef table, carried in <see cref="DeclarationOrder.Position"/>.
    /// For this compilation the compiler emits a namespace's own types, in declaration order, before any of its
    /// child namespaces, and child namespaces in the reverse of the order they were first declared. Without the
    /// declarations of namespaces that hold no handler, "first declared" is approximated by the earliest handler
    /// inside each one.
    /// </remarks>
    private sealed class MetadataOrder : IComparer<DeclarationOrder>
    {
        private readonly Dictionary<string, (int Tree, int Position)> _firstInNamespace = new(StringComparer.Ordinal);

        public MetadataOrder(IEnumerable<DeclarationOrder> orders)
        {
            foreach (DeclarationOrder order in orders.Where(static order => order.Assembly == 0))
            {
                // Every enclosing namespace is "first declared" no later than anything inside it.
                string ns = order.Namespace;
                while (true)
                {
                    (int, int) here = (order.Tree, order.Position);
                    if (!_firstInNamespace.TryGetValue(ns, out (int Tree, int Position) known) || Compare(here, known) < 0)
                    {
                        _firstInNamespace[ns] = here;
                    }

                    if (ns.Length == 0)
                    {
                        break;
                    }

                    int dot = ns.LastIndexOf('.');
                    ns = dot < 0 ? string.Empty : ns.Substring(0, dot);
                }
            }
        }

        public int Compare(DeclarationOrder? x, DeclarationOrder? y)
        {
            if (x is null || y is null)
            {
                return x is null ? (y is null ? 0 : -1) : 1;
            }

            if (x.Assembly != y.Assembly)
            {
                return x.Assembly.CompareTo(y.Assembly);
            }

            if (x.Assembly != 0)
            {
                return x.Position.CompareTo(y.Position);
            }

            // Every top-level type is written before any nested one, then each level in its parents' order: a
            // probe of DefinedTypes found it, and the differential scenario in NamespaceOrder.cs holds it.
            if (x.Nesting.Length != y.Nesting.Length)
            {
                return x.Nesting.Length.CompareTo(y.Nesting.Length);
            }

            int outer = CompareTopLevel(x, y);
            for (int level = 0; outer == 0 && level < x.Nesting.Length; level++)
            {
                outer = x.Nesting[level].CompareTo(y.Nesting[level]);
            }

            return outer;
        }

        private int CompareTopLevel(DeclarationOrder x, DeclarationOrder y)
        {
            if (x.Namespace == y.Namespace)
            {
                return Compare((x.Tree, x.Position), (y.Tree, y.Position));
            }

            string[] left = x.Namespace.Length == 0 ? [] : x.Namespace.Split('.');
            string[] right = y.Namespace.Length == 0 ? [] : y.Namespace.Split('.');
            int common = 0;
            while (common < left.Length && common < right.Length && left[common] == right[common])
            {
                common++;
            }

            // A namespace's own types come before anything in its child namespaces.
            if (common == left.Length)
            {
                return -1;
            }

            if (common == right.Length)
            {
                return 1;
            }

            // Sibling namespaces come out in the reverse of the order they were first declared in: the compiler
            // walks namespaces with a stack. A probe during the generic handler work found it; the differential
            // scenario in NamespaceOrder.cs holds it.
            string leftChild = string.Join(".", left, 0, common + 1);
            string rightChild = string.Join(".", right, 0, common + 1);
            return Compare(_firstInNamespace[rightChild], _firstInNamespace[leftChild]);
        }

        private static int Compare((int Tree, int Position) x, (int Tree, int Position) y) =>
            x.Tree != y.Tree ? x.Tree.CompareTo(y.Tree) : x.Position.CompareTo(y.Position);
    }

    private static string ProcessorService(HandlerModel processor, string requestType) =>
        processor.Kind == HandlerKind.PreProcessor
            ? $"global::Relevox.Pipeline.IRequestPreProcessor<{requestType}>"
            : $"global::Relevox.Pipeline.IRequestPostProcessor<{requestType}, {processor.ResponseType}>";

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
    /// Where <c>AddRelevox</c> is declared: <c>Microsoft.Extensions.DependencyInjection</c>, where MediatR's
    /// <c>AddMediatR</c> is, so a migrated <c>Program.cs</c> compiles without a new <c>using</c>.
    /// </summary>
    /// <remarks>
    /// The exception is a project that can see another project's <c>AddRelevox</c> there, which happens when a
    /// test project references Relevox and the app it tests gives it InternalsVisibleTo. Two methods with one
    /// signature in one namespace would make every call ambiguous (CS0121), so this project's method moves to
    /// its own namespace. Code inside that namespace finds it first; code outside gets the other project's.
    /// </remarks>
    public static string RegistrationNamespaceFor(string assemblyName, bool seesAnotherRegistration) =>
        seesAnotherRegistration ? ToNamespace(assemblyName) : "Microsoft.Extensions.DependencyInjection";

    /// <summary>
    /// The assembly name, made into a valid namespace.
    /// </summary>
    /// <remarks>
    /// The generated mediator is internal and lives in a namespace named after its assembly, so two projects
    /// that both reference Relevox, one with InternalsVisibleTo the other, never declare the same type name.
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
