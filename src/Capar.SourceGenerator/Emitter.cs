using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Capar.SourceGenerator;

/// <summary>
/// Writes the generated mediator and its registration method.
/// </summary>
/// <remarks>
/// <para>
/// The emitted code is written to be the code a careful person would write by hand for this exact set of
/// handlers. Every request gets a typed <c>Send</c> overload that resolves its handler and calls it, with no
/// delegate, no closure and no await in between, so the task the handler returns is the task the caller
/// gets. The interface methods, for callers holding only <c>IRequest&lt;T&gt;</c>, look the runtime type up
/// once in a frozen dictionary and call one small dispatcher per request, which keeps them the same size
/// however many requests exist.
/// </para>
/// <para>
/// Everything is fully qualified with <c>global::</c>, because the generated file lands in the user's
/// namespace and any type there could shadow a name it relies on.
/// </para>
/// </remarks>
internal static class Emitter
{
    private const string Task = "global::System.Threading.Tasks.Task";
    private const string Token = "global::System.Threading.CancellationToken";
    private const string Type = "global::System.Type";
    private const string AsyncEnumerable = "global::System.Collections.Generic.IAsyncEnumerable";
    private const string Frozen = "global::System.Collections.Frozen";
    private const string DependencyInjection = "global::Microsoft.Extensions.DependencyInjection";

    private static readonly string Version =
        typeof(Emitter).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";

    public static string Mediator(EmitPlan plan)
    {
        Dictionary<string, int> handlerIndex = plan.HandlerTypes
            .Select(static (type, index) => (type, index))
            .ToDictionary(static pair => pair.type, static pair => pair.index, System.StringComparer.Ordinal);

        CodeWriter code = Header(plan);
        code.Open($"namespace {plan.Namespace}");

        code.Line("/// <summary>");
        code.Line("/// The mediator Capar generated for this assembly, from the handlers it could see when it was built.");
        code.Line("/// </summary>");
        code.Line("/// <remarks>");
        code.Line("/// Take this type as a dependency instead of <c>IMediator</c> where the call site knows what it sends:");
        code.Line("/// its typed overloads call the handler directly, with nothing to look up.");
        code.Line("/// </remarks>");
        code.Line($"[global::System.CodeDom.Compiler.GeneratedCode(\"Capar\", \"{Version}\")]");
        code.Open("internal sealed partial class CaparMediator : global::Capar.IMediator");

        code.Line("private readonly global::System.IServiceProvider _services;");
        code.Line("private readonly global::Capar.Internal.PipelineState<CaparMediator> _pipelines;");
        foreach (KeyValuePair<string, int> handler in handlerIndex.OrderBy(static pair => pair.Value))
        {
            code.Line($"private readonly bool _keep{handler.Value};");
            code.Line($"private {handler.Key}? _handler{handler.Value};");
        }

        code.Line();
        code.Line("/// <summary>Creates the mediator over the container it resolves handlers from.</summary>");
        code.Line("/// <param name=\"services\">The container, or the scope, handlers come from.</param>");
        code.Line("/// <param name=\"keep\">");
        code.Line("/// One flag per handler, in the order <c>AddCapar</c> registers them: true when the handler is registered");
        code.Line("/// as scoped or singleton, so the container would hand this mediator the same instance every time anyway");
        code.Line("/// and the first one can be kept.");
        code.Line("/// </param>");
        code.Line("/// <param name=\"pipelines\">Which requests this container has no behaviours for, learned on first use.</param>");
        code.Open("public CaparMediator(global::System.IServiceProvider services, bool[] keep, global::Capar.Internal.PipelineState<CaparMediator> pipelines)");
        code.Line("global::System.ArgumentNullException.ThrowIfNull(keep);");
        code.Line("_services = services;");
        code.Line("_pipelines = pipelines;");
        foreach (KeyValuePair<string, int> handler in handlerIndex.OrderBy(static pair => pair.Value))
        {
            code.Line($"_keep{handler.Value} = keep[{handler.Value}];");
        }

        code.Close();

        EmitRequests(code, plan, handlerIndex);
        EmitStreams(code, plan, handlerIndex);
        EmitNotifications(code, plan, handlerIndex);
        EmitInterfaceMembers(code);
        EmitHandlerAccessors(code, handlerIndex);
        EmitDispatchers(code, plan);

        code.Close();
        code.Close();
        return code.ToString();
    }

    public static string Registration(EmitPlan plan)
    {
        const string TryAdd = DependencyInjection + ".Extensions.ServiceCollectionDescriptorExtensions.TryAdd";

        CodeWriter code = Header(plan);
        code.Open($"namespace {plan.Namespace}");
        code.Line("/// <summary>Registers the mediator Capar generated for this assembly.</summary>");
        code.Line($"[global::System.CodeDom.Compiler.GeneratedCode(\"Capar\", \"{Version}\")]");
        code.Open("internal static class CaparServiceCollectionExtensions");
        code.Line("/// <summary>");
        code.Line("/// Registers every handler found at build time, by its concrete type, and the generated mediator as");
        code.Line("/// <c>IMediator</c>, <c>ISender</c> and <c>IPublisher</c>. Nothing is scanned at run time.");
        code.Line("/// </summary>");
        code.Line("/// <remarks>");
        code.Line("/// Calling it twice is harmless. Calling it after another project's <c>AddCapar</c> throws, because only one");
        code.Line("/// mediator can be <c>IMediator</c> and silently keeping the first would leave this project's handlers");
        code.Line("/// unreachable.");
        code.Line("/// </remarks>");
        code.Line("/// <param name=\"services\">The service collection.</param>");
        code.Line("/// <param name=\"configure\">Adjusts the options, such as the handler lifetime.</param>");
        code.Line("/// <returns>The same service collection.</returns>");
        code.Open($"public static {DependencyInjection}.IServiceCollection AddCapar(this {DependencyInjection}.IServiceCollection services, global::System.Action<global::Capar.CaparOptions>? configure = null)");
        code.Line("global::System.ArgumentNullException.ThrowIfNull(services);");
        code.Open($"foreach ({DependencyInjection}.ServiceDescriptor existing in services)");
        code.Line("if (existing.ServiceType == typeof(CaparMediator))");
        code.Line("{");
        code.Line("    return services;");
        code.Line("}");
        code.Line();
        code.Line("if (existing.ServiceType == typeof(global::Capar.IMediator))");
        code.Line("{");
        code.Line("    throw new global::System.InvalidOperationException(");
        code.Line($"        \"IMediator is already registered, probably by AddCapar from another project. Call AddCapar once, from the project whose handlers it should reach (usually the host), and have the other projects reference Capar.Contracts rather than Capar. This call came from '{plan.Namespace}'.\");");
        code.Line("}");
        code.Close();
        code.Line();
        code.Line("global::Capar.CaparOptions options = new();");
        code.Line("configure?.Invoke(options);");
        code.Line($"{DependencyInjection}.ServiceLifetime lifetime = options.Lifetime;");
        code.Line($"{DependencyInjection}.ServiceLifetime handlerLifetime = options.HandlerLifetime;");
        code.Line();

        // Each registration names its type with typeof at the call, rather than passing a Type through a
        // helper. The ServiceDescriptor constructor is annotated for trimming, and a literal typeof is what
        // lets the trimmer keep the constructor without a warning in the user's NativeAOT build.
        foreach (string handler in plan.HandlerTypes)
        {
            code.Line($"{TryAdd}(services, new {DependencyInjection}.ServiceDescriptor(typeof({handler}), typeof({handler}), handlerLifetime));");
        }

        code.Line();
        code.Line("// Whether each handler can be kept is read from the registration actually in force, which may be one the");
        code.Line("// application made before this call and TryAdd left alone. A transient handler is never kept.");
        code.Line($"bool[] keep = new bool[{plan.HandlerTypes.Length}];");
        for (int index = 0; index < plan.HandlerTypes.Length; index++)
        {
            code.Line($"keep[{index}] = IsShared(services, typeof({plan.HandlerTypes[index]}));");
        }

        code.Line();
        int requestCount = plan.Requests.Count(static request => !request.Request.IsAbstract);
        int streamCount = plan.Streams.Count(static stream => !stream.Request.IsAbstract);
        code.Line($"{TryAdd}(services, new {DependencyInjection}.ServiceDescriptor(typeof(global::Capar.Internal.PipelineState<CaparMediator>), static _ => new global::Capar.Internal.PipelineState<CaparMediator>({requestCount}, {streamCount}), {DependencyInjection}.ServiceLifetime.Singleton));");
        code.Line();
        code.Line("if (options.AutoRegisterRequestProcessors)");
        code.Line("{");
        foreach (ServiceRegistration processor in plan.Processors)
        {
            code.Line($"    {DependencyInjection}.Extensions.ServiceCollectionDescriptorExtensions.TryAddEnumerable(services, new {DependencyInjection}.ServiceDescriptor(typeof({processor.ServiceType}), typeof({processor.ImplementationType}), handlerLifetime));");
        }

        foreach (OpenRegistration processor in plan.OpenProcessors)
        {
            code.Line($"    {DependencyInjection}.Extensions.ServiceCollectionDescriptorExtensions.TryAddEnumerable(services, new {DependencyInjection}.ServiceDescriptor(typeof({processor.ServiceType}), typeof({processor.ImplementationType}), handlerLifetime));");
        }

        code.Line("}");
        code.Line();

        code.Open($"foreach ({DependencyInjection}.ServiceDescriptor processor in global::System.Linq.Enumerable.Concat(options.RequestPreProcessorsToRegister, options.RequestPostProcessorsToRegister))");
        code.Line($"{DependencyInjection}.Extensions.ServiceCollectionDescriptorExtensions.TryAddEnumerable(services, processor);");
        code.Close();
        code.Line();

        // The processor behaviours sit ahead of every user behaviour, pre before post, as MediatR registers them.
        // They are registered closed per request rather than as an open generic, so nothing has to be closed at
        // run time.
        //
        // MediatR 12 adds a processor behaviour only when a processor of that kind was added through its
        // configuration. Processors it found by scanning, or that the application registered with the container
        // itself, are registered but never run unless one was also added explicitly. That is surprising, and it
        // is matched on purpose: code moving from MediatR must not find processors running that never ran.
        RequestEntry[] dispatched = [.. plan.Requests.Where(static request => !request.Request.IsAbstract)];
        foreach ((string list, string behavior) in new[] { ("RequestPreProcessorsToRegister", "RequestPreProcessorBehavior"), ("RequestPostProcessorsToRegister", "RequestPostProcessorBehavior") })
        {
            code.Line($"if (options.{list}.Count > 0)");
            code.Line("{");
            foreach (RequestEntry request in dispatched)
            {
                string response = request.ResponseType ?? "global::Capar.Unit";
                code.Line($"    {DependencyInjection}.Extensions.ServiceCollectionDescriptorExtensions.TryAddEnumerable(services, new {DependencyInjection}.ServiceDescriptor(typeof(global::Capar.IPipelineBehavior<{request.Request.Type}, {response}>), typeof(global::Capar.Pipeline.{behavior}<{request.Request.Type}, {response}>), {DependencyInjection}.ServiceLifetime.Transient));");
            }

            code.Line("}");
            code.Line();
        }

        code.Line("// Behaviours are resolved from the container on every send, as in MediatR, so these are simply registered.");
        code.Line("// TryAddEnumerable keeps a behaviour added twice from running twice.");
        code.Open($"foreach ({DependencyInjection}.ServiceDescriptor behavior in options.BehaviorsToRegister)");
        code.Line($"{DependencyInjection}.Extensions.ServiceCollectionDescriptorExtensions.TryAddEnumerable(services, behavior);");
        code.Close();
        code.Line();
        code.Line($"{TryAdd}(services, new {DependencyInjection}.ServiceDescriptor(typeof(CaparMediator), provider => new CaparMediator(provider, keep, {DependencyInjection}.ServiceProviderServiceExtensions.GetRequiredService<global::Capar.Internal.PipelineState<CaparMediator>>(provider)), lifetime));");
        foreach (string contract in new[] { "global::Capar.IMediator", "global::Capar.ISender", "global::Capar.IPublisher" })
        {
            code.Line($"{TryAdd}(services, new {DependencyInjection}.ServiceDescriptor(typeof({contract}), static provider => {DependencyInjection}.ServiceProviderServiceExtensions.GetRequiredService<CaparMediator>(provider), lifetime));");
        }

        code.Line("return services;");
        code.Close();


        code.Line();
        code.Open($"private static bool IsShared({DependencyInjection}.IServiceCollection services, {Type} type)");
        code.Line($"{DependencyInjection}.ServiceDescriptor? last = null;");
        code.Open($"foreach ({DependencyInjection}.ServiceDescriptor descriptor in services)");
        code.Line("if (descriptor.ServiceType == type)");
        code.Line("{");
        code.Line("    last = descriptor;");
        code.Line("}");
        code.Close();
        code.Line();
        code.Line($"return last is not null && last.Lifetime != {DependencyInjection}.ServiceLifetime.Transient;");
        code.Close();

        code.Close();
        code.Close();
        return code.ToString();
    }

    private static CodeWriter Header(EmitPlan plan)
    {
        CodeWriter code = new();
        code.Line("// <auto-generated/>");
        code.Line("#nullable enable");

        // The generated code names the user's own types. When one of them is obsolete or experimental, the user
        // has already accepted that where they declared and registered it; the same warning raised again from
        // a file they cannot edit would only break builds that treat warnings as errors.
        code.Line("#pragma warning disable CS0612, CS0618");
        if (plan.SuppressedWarnings.Length > 0)
        {
            code.Line($"#pragma warning disable {string.Join(", ", plan.SuppressedWarnings)}");
        }

        code.Line();
        return code;
    }

    private static void EmitRequests(CodeWriter code, EmitPlan plan, Dictionary<string, int> handlerIndex)
    {
        RequestEntry[] requests = [.. plan.Requests.Where(static request => !request.Request.IsAbstract)];
        for (int position = 0; position < requests.Length; position++)
        {
            RequestEntry request = requests[position];
            int handler = handlerIndex[request.HandlerType];
            bool hasResponse = request.ResponseSyntax is not null;
            string returns = hasResponse ? $"{Task}<{request.ResponseSyntax}>" : Task;
            string contract = hasResponse
                ? $"global::Capar.IRequestHandler<{request.Request.Syntax}, {request.ResponseSyntax}>"
                : $"global::Capar.IRequestHandler<{request.Request.Syntax}>";

            code.Line();
            code.Line($"/// <summary>Sends <c>{Display(request.Request.Type)}</c> straight to <c>{Display(request.HandlerType)}</c>.</summary>");
            code.Line("/// <param name=\"request\">The request.</param>");
            code.Line("/// <param name=\"cancellationToken\">Cancels the work.</param>");
            code.Line("/// <returns>The handler's task, returned as is.</returns>");
            code.Open($"public {returns} Send({request.Request.Syntax} request, {Token} cancellationToken = default)");
            NullCheck(code, "request", request.Request);
            RuntimeTypeCheck(
                code,
                "request",
                request.Request,
                hasResponse
                    ? $"Send<{request.ResponseSyntax}>((global::Capar.IRequest<{request.ResponseSyntax}>)request, cancellationToken)"
                    : "Send<global::Capar.IRequest>(request, cancellationToken)");
            string response = request.ResponseSyntax ?? "global::Capar.Unit";
            code.Line($"global::Capar.IPipelineBehavior<{request.Request.Syntax}, {response}>[]? behaviors = _pipelines.Behaviors<{request.Request.Syntax}, {response}>({position}, _services);");
            code.Line("if (behaviors is null)");
            code.Line("{");
            code.Line($"    return (({contract})Handler{handler}()).Handle(request, cancellationToken);");
            code.Line("}");
            code.Line();

            // The handler is resolved inside the innermost step, so a behaviour that short-circuits never causes
            // it to be constructed, as in MediatR. A request without a response runs through behaviours typed
            // over Unit, which is how MediatR 12 models it too.
            code.Line(hasResponse
                ? $"return global::Capar.Internal.RequestPipeline.Run(this, request, behaviors, static (mediator, message, token) => (({contract})mediator.Handler{handler}()).Handle(message, token), cancellationToken);"
                : $"return global::Capar.Internal.RequestPipeline.Run(this, request, behaviors, static async (mediator, message, token) => {{ await (({contract})mediator.Handler{handler}()).Handle(message, token).ConfigureAwait(false); return global::Capar.Unit.Value; }}, cancellationToken);");
            code.Close();
        }
    }

    private static void EmitStreams(CodeWriter code, EmitPlan plan, Dictionary<string, int> handlerIndex)
    {
        for (int index = 0; index < plan.Streams.Length; index++)
        {
            StreamEntry stream = plan.Streams[index];
            if (stream.Request.IsAbstract)
            {
                continue;
            }

            int handler = handlerIndex[stream.HandlerType];
            code.Line();
            code.Line($"/// <summary>Creates the stream for <c>{Display(stream.Request.Type)}</c> from <c>{Display(stream.HandlerType)}</c>.</summary>");
            code.Line("/// <param name=\"request\">The request.</param>");
            code.Line("/// <param name=\"cancellationToken\">Cancels the enumeration.</param>");
            code.Line("/// <returns>The stream. The handler is resolved and called when enumeration starts.</returns>");
            code.Open($"public {AsyncEnumerable}<{stream.ResponseSyntax}> CreateStream({stream.Request.Syntax} request, {Token} cancellationToken = default)");
            NullCheck(code, "request", stream.Request);
            RuntimeTypeCheck(code, "request", stream.Request, $"CreateStream<{stream.ResponseSyntax}>((global::Capar.IStreamRequest<{stream.ResponseSyntax}>)request, cancellationToken)");
            code.Line($"return Stream{index}(request, cancellationToken);");
            code.Close();

            // An iterator rather than the handler's own stream, for two reasons MediatR 12 shares. The handler is
            // resolved when enumeration starts, so enumerating twice gets a fresh transient handler. And the token
            // reaching the handler is the combined one, so a caller's WithCancellation cancels a handler that
            // does not declare [EnumeratorCancellation] itself.
            code.Line();
            code.Open($"private async {AsyncEnumerable}<{stream.ResponseSyntax}> Stream{index}({stream.Request.Syntax} request, [global::System.Runtime.CompilerServices.EnumeratorCancellation] {Token} cancellationToken)");
            code.Line($"{AsyncEnumerable}<{stream.ResponseSyntax}> items = ((global::Capar.IStreamRequestHandler<{stream.Request.Syntax}, {stream.ResponseSyntax}>)Handler{handler}()).Handle(request, cancellationToken);");
            code.Open($"await foreach ({stream.ResponseSyntax} item in global::System.Threading.Tasks.TaskAsyncEnumerableExtensions.WithCancellation(items, cancellationToken).ConfigureAwait(false))");
            code.Line("yield return item;");
            code.Close();
            code.Close();
        }
    }

    private static void EmitNotifications(CodeWriter code, EmitPlan plan, Dictionary<string, int> handlerIndex)
    {
        for (int index = 0; index < plan.Notifications.Length; index++)
        {
            NotificationEntry notification = plan.Notifications[index];
            MessageShape shape = notification.Notification;

            code.Line();
            code.Line($"/// <summary>Publishes <c>{Display(shape.Type)}</c> to its {notification.Targets.Length} handler(s), one after another.</summary>");
            code.Line("/// <param name=\"notification\">The notification.</param>");
            code.Line("/// <param name=\"cancellationToken\">Cancels the work.</param>");
            code.Line("/// <returns>A task that completes when every handler has finished.</returns>");
            code.Open($"public {Task} Publish({shape.Syntax} notification, {Token} cancellationToken = default)");
            NullCheck(code, "notification", shape);
            RuntimeTypeCheck(code, "notification", shape, "Publish((object)notification, cancellationToken)");

            if (notification.Targets.Length == 0)
            {
                code.Line($"return {Task}.CompletedTask;");
                code.Close();
                continue;
            }

            // Every handler is resolved before the first one runs, as MediatR does, so a handler whose
            // construction fails throws out of Publish rather than after earlier handlers have already run.
            List<string> arguments = ["notification"];
            for (int target = 0; target < notification.Targets.Length; target++)
            {
                NotificationTarget handler = notification.Targets[target];
                code.Line($"global::Capar.INotificationHandler<{handler.HandledSyntax}> handler{target} = Handler{handlerIndex[handler.HandlerType]}();");
                arguments.Add($"handler{target}");
            }

            arguments.Add("cancellationToken");
            code.Line($"return PublishSequentially{index}({string.Join(", ", arguments)});");
            code.Close();

            // Async even for one handler, so an exception a handler throws before its first await ends up in
            // the returned task rather than escaping from Publish, as with MediatR's default publisher. When
            // every handler completes synchronously this allocates nothing: the builder returns a cached task.
            List<string> parameters = [$"{shape.Syntax} notification"];
            for (int target = 0; target < notification.Targets.Length; target++)
            {
                parameters.Add($"global::Capar.INotificationHandler<{notification.Targets[target].HandledSyntax}> handler{target}");
            }

            parameters.Add($"{Token} cancellationToken");
            code.Line();
            code.Open($"private static async {Task} PublishSequentially{index}({string.Join(", ", parameters)})");
            for (int target = 0; target < notification.Targets.Length; target++)
            {
                code.Line($"await handler{target}.Handle(notification, cancellationToken).ConfigureAwait(false);");
            }

            code.Close();
        }
    }

    private static void EmitInterfaceMembers(CodeWriter code)
    {
        code.Line();
        code.Line("/// <inheritdoc />");
        code.Open($"public {Task}<TResponse> Send<TResponse>(global::Capar.IRequest<TResponse> request, {Token} cancellationToken = default)");
        code.Line("global::System.ArgumentNullException.ThrowIfNull(request);");
        code.Line($"{Type} type = request.GetType();");
        code.Line("return Requests.TryGetValue(type, out RequestDispatcher? dispatcher) && dispatcher is RequestDispatcher<TResponse> typed");
        code.Line("    ? typed.Send(this, request, cancellationToken)");
        code.Line("    : throw NoHandler(type);");
        code.Close();

        code.Line();
        code.Line("/// <inheritdoc />");
        code.Line($"public {Task} Send<TRequest>(TRequest request, {Token} cancellationToken = default)");
        code.Open("    where TRequest : global::Capar.IRequest");
        code.Line("if (request is null)");
        code.Line("{");
        code.Line("    throw new global::System.ArgumentNullException(nameof(request));");
        code.Line("}");
        code.Line();
        code.Line($"{Type} type = request.GetType();");
        code.Line("return Requests.TryGetValue(type, out RequestDispatcher? dispatcher) && dispatcher is VoidRequestDispatcher typed");
        code.Line("    ? typed.Send(this, request, cancellationToken)");
        code.Line("    : throw NoHandler(type);");
        code.Close();

        code.Line();
        code.Line("/// <inheritdoc />");
        code.Open($"public {Task}<object?> Send(object request, {Token} cancellationToken = default)");
        code.Line("global::System.ArgumentNullException.ThrowIfNull(request);");
        code.Line($"{Type} type = request.GetType();");
        code.Line("if (Requests.TryGetValue(type, out RequestDispatcher? dispatcher))");
        code.Line("{");
        code.Line("    return dispatcher.SendObject(this, request, cancellationToken);");
        code.Line("}");
        code.Line();
        code.Line("// MediatR throws for something that is not a request, and faults the task for a request it cannot");
        code.Line("// handle, because only the second goes through its asynchronous wrapper.");
        code.Line("return request is global::Capar.IBaseRequest");
        code.Line($"    ? {Task}.FromException<object?>(NoHandler(type))");
        code.Line("    : throw new global::System.ArgumentException($\"'{type}' does not implement IRequest or IRequest<TResponse>.\", nameof(request));");
        code.Close();

        code.Line();
        code.Line("/// <inheritdoc />");
        code.Open($"public {AsyncEnumerable}<TResponse> CreateStream<TResponse>(global::Capar.IStreamRequest<TResponse> request, {Token} cancellationToken = default)");
        code.Line("global::System.ArgumentNullException.ThrowIfNull(request);");
        code.Line($"{Type} type = request.GetType();");
        code.Line("return Streams.TryGetValue(type, out StreamDispatcher? dispatcher) && dispatcher is StreamDispatcher<TResponse> typed");
        code.Line("    ? typed.Create(this, request, cancellationToken)");
        code.Line("    : Fail<TResponse>(NoHandler(type));");
        code.Close();

        code.Line();
        code.Line("/// <inheritdoc />");
        code.Open($"public {AsyncEnumerable}<object?> CreateStream(object request, {Token} cancellationToken = default)");
        code.Line("global::System.ArgumentNullException.ThrowIfNull(request);");
        code.Line($"{Type} type = request.GetType();");
        code.Line("if (Streams.TryGetValue(type, out StreamDispatcher? dispatcher))");
        code.Line("{");
        code.Line("    return dispatcher.CreateObject(this, request, cancellationToken);");
        code.Line("}");
        code.Line();
        code.Line("// MediatR throws straight away for something that is not a stream request, and fails on enumeration for");
        code.Line("// one it cannot handle.");
        code.Line("return IsStreamRequest(type)");
        code.Line("    ? Fail<object?>(NoHandler(type))");
        code.Line("    : throw new global::System.ArgumentException($\"'{type}' does not implement IStreamRequest<TResponse>.\", nameof(request));");
        code.Close();

        code.Line();
        code.Line("// Reflection, but only on a path that is about to throw anyway, to choose which exception. Should trimming");
        code.Line("// remove the interface, the worst outcome is ArgumentException where InvalidOperationException was due.");
        code.Line("[global::System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(\"Trimming\", \"IL2070\", Justification = \"Error path only; see the comment above.\")]");
        code.Open($"private static bool IsStreamRequest({Type} type)");
        code.Open($"foreach ({Type} contract in type.GetInterfaces())");
        code.Line("if (contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(global::Capar.IStreamRequest<>))");
        code.Line("{");
        code.Line("    return true;");
        code.Line("}");
        code.Close();
        code.Line();
        code.Line("return false;");
        code.Close();

        code.Line();
        code.Line("/// <inheritdoc />");
        code.Open($"public {Task} Publish(object notification, {Token} cancellationToken = default)");
        code.Line("global::System.ArgumentNullException.ThrowIfNull(notification);");
        code.Line("if (Notifications.TryGetValue(notification.GetType(), out NotificationDispatcher? dispatcher))");
        code.Line("{");
        code.Line("    return dispatcher.Publish(this, notification, cancellationToken);");
        code.Line("}");
        code.Line();
        code.Line("return notification is global::Capar.INotification");
        code.Line($"    ? {Task}.CompletedTask");
        code.Line("    : throw new global::System.ArgumentException($\"'{notification.GetType()}' does not implement INotification.\", nameof(notification));");
        code.Close();

        code.Line();
        code.Line("/// <inheritdoc />");
        code.Line($"public {Task} Publish<TNotification>(TNotification notification, {Token} cancellationToken = default)");
        code.Open("    where TNotification : global::Capar.INotification");
        code.Line("if (notification is null)");
        code.Line("{");
        code.Line("    throw new global::System.ArgumentNullException(nameof(notification));");
        code.Line("}");
        code.Line();
        code.Line("return Notifications.TryGetValue(notification.GetType(), out NotificationDispatcher? dispatcher)");
        code.Line("    ? dispatcher.Publish(this, notification, cancellationToken)");
        code.Line($"    : {Task}.CompletedTask;");
        code.Close();
    }

    private static void EmitHandlerAccessors(CodeWriter code, Dictionary<string, int> handlerIndex)
    {
        foreach (KeyValuePair<string, int> handler in handlerIndex.OrderBy(static pair => pair.Value))
        {
            code.Line();
            code.Line("[global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
            code.Line($"private {handler.Key} Handler{handler.Value}() =>");
            code.Line($"    _keep{handler.Value} ? (_handler{handler.Value} ??= Resolve<{handler.Key}>()) : Resolve<{handler.Key}>();");
        }

        code.Line();
        code.Line("private T Resolve<T>() where T : notnull =>");
        code.Line($"    {DependencyInjection}.ServiceProviderServiceExtensions.GetRequiredService<T>(_services);");
        code.Line();
        code.Line("// A stream with no handler fails when it is enumerated, not when it is created, as it does in MediatR 12.");
        code.Open($"private static async {AsyncEnumerable}<T> Fail<T>(global::System.Exception exception)");
        code.Line($"await {Task}.CompletedTask.ConfigureAwait(false);");
        code.Line("if (exception is not null)");
        code.Line("{");
        code.Line("    throw exception;");
        code.Line("}");
        code.Line();
        code.Line("yield break;");
        code.Close();
        code.Line();
        code.Line("private static global::System.InvalidOperationException NoHandler(global::System.Type type) =>");
        code.Line("    new($\"No handler for '{type}' was found when this assembly was built. Capar finds handlers at compile time, so the handler has to be in this project or in a project it references.\");");
    }

    private static void EmitDispatchers(CodeWriter code, EmitPlan plan)
    {
        RequestEntry[] requests = [.. plan.Requests.Where(static request => !request.Request.IsAbstract)];
        (StreamEntry Stream, int Index)[] streams =
        [
            .. plan.Streams
                .Select(static (stream, index) => (stream, index))
                .Where(static pair => !pair.stream.Request.IsAbstract),
        ];

        code.Line();
        code.Line("// One small dispatcher per message type, found by the message's runtime type. The interface methods");
        code.Line("// above stay the same size however many messages exist, which is what keeps that path from slowing");
        code.Line("// down as a project grows.");
        code.Line($"private static readonly {Frozen}.FrozenDictionary<{Type}, RequestDispatcher> Requests = {Frozen}.FrozenDictionary.ToFrozenDictionary(new global::System.Collections.Generic.KeyValuePair<{Type}, RequestDispatcher>[]");
        code.Line("{");
        for (int index = 0; index < requests.Length; index++)
        {
            code.Line($"    new(typeof({requests[index].Request.Type}), new Request{index}()),");
        }

        code.Line("});");
        code.Line();
        code.Line($"private static readonly {Frozen}.FrozenDictionary<{Type}, StreamDispatcher> Streams = {Frozen}.FrozenDictionary.ToFrozenDictionary(new global::System.Collections.Generic.KeyValuePair<{Type}, StreamDispatcher>[]");
        code.Line("{");
        foreach ((StreamEntry stream, int index) in streams)
        {
            code.Line($"    new(typeof({stream.Request.Type}), new StreamDispatcher{index}()),");
        }

        code.Line("});");
        code.Line();
        code.Line($"private static readonly {Frozen}.FrozenDictionary<{Type}, NotificationDispatcher> Notifications = {Frozen}.FrozenDictionary.ToFrozenDictionary(new global::System.Collections.Generic.KeyValuePair<{Type}, NotificationDispatcher>[]");
        code.Line("{");
        for (int index = 0; index < plan.Notifications.Length; index++)
        {
            code.Line($"    new(typeof({plan.Notifications[index].Notification.Type}), new Notification{index}()),");
        }

        code.Line("});");

        code.Line();
        code.Open("private abstract class RequestDispatcher");
        code.Line($"public abstract {Task}<object?> SendObject(CaparMediator mediator, object request, {Token} cancellationToken);");
        code.Close();

        code.Line();
        code.Open("private abstract class RequestDispatcher<TResponse> : RequestDispatcher");
        code.Line($"public abstract {Task}<TResponse> Send(CaparMediator mediator, global::Capar.IRequest<TResponse> request, {Token} cancellationToken);");
        code.Line();
        code.Line($"public sealed override async {Task}<object?> SendObject(CaparMediator mediator, object request, {Token} cancellationToken) =>");
        code.Line("    await Send(mediator, (global::Capar.IRequest<TResponse>)request, cancellationToken).ConfigureAwait(false);");
        code.Close();

        code.Line();
        code.Open("private abstract class VoidRequestDispatcher : RequestDispatcher");
        code.Line($"public abstract {Task} Send(CaparMediator mediator, global::Capar.IRequest request, {Token} cancellationToken);");
        code.Line();
        code.Open($"public sealed override async {Task}<object?> SendObject(CaparMediator mediator, object request, {Token} cancellationToken)");
        code.Line("await Send(mediator, (global::Capar.IRequest)request, cancellationToken).ConfigureAwait(false);");
        code.Line();
        code.Line("// MediatR 12 answers a request without a response with a boxed Unit here, not null.");
        code.Line("return global::Capar.Unit.Value;");
        code.Close();
        code.Close();

        code.Line();
        code.Open("private abstract class StreamDispatcher");
        code.Line($"public abstract {AsyncEnumerable}<object?> CreateObject(CaparMediator mediator, object request, {Token} cancellationToken);");
        code.Close();

        code.Line();
        code.Open("private abstract class StreamDispatcher<TResponse> : StreamDispatcher");
        code.Line($"public abstract {AsyncEnumerable}<TResponse> Create(CaparMediator mediator, global::Capar.IStreamRequest<TResponse> request, {Token} cancellationToken);");
        code.Line();
        code.Line($"public sealed override {AsyncEnumerable}<object?> CreateObject(CaparMediator mediator, object request, {Token} cancellationToken) =>");
        code.Line("    Box(Create(mediator, (global::Capar.IStreamRequest<TResponse>)request, cancellationToken), cancellationToken);");
        code.Line();
        code.Open($"private static async {AsyncEnumerable}<object?> Box({AsyncEnumerable}<TResponse> items, [global::System.Runtime.CompilerServices.EnumeratorCancellation] {Token} cancellationToken)");
        code.Open("await foreach (TResponse item in global::System.Threading.Tasks.TaskAsyncEnumerableExtensions.WithCancellation(items, cancellationToken).ConfigureAwait(false))");
        code.Line("yield return item;");
        code.Close();
        code.Close();
        code.Close();

        code.Line();
        code.Open("private abstract class NotificationDispatcher");
        code.Line($"public abstract {Task} Publish(CaparMediator mediator, object notification, {Token} cancellationToken);");
        code.Close();

        for (int index = 0; index < requests.Length; index++)
        {
            RequestEntry request = requests[index];
            code.Line();
            if (request.ResponseSyntax is null)
            {
                code.Open($"private sealed class Request{index} : VoidRequestDispatcher");
                code.Line($"public override {Task} Send(CaparMediator mediator, global::Capar.IRequest request, {Token} cancellationToken) =>");
            }
            else
            {
                code.Open($"private sealed class Request{index} : RequestDispatcher<{request.ResponseSyntax}>");
                code.Line($"public override {Task}<{request.ResponseSyntax}> Send(CaparMediator mediator, global::Capar.IRequest<{request.ResponseSyntax}> request, {Token} cancellationToken) =>");
            }

            code.Line($"    mediator.Send(({request.Request.Syntax})request, cancellationToken);");
            code.Close();
        }

        foreach ((StreamEntry stream, int index) in streams)
        {
            code.Line();
            code.Open($"private sealed class StreamDispatcher{index} : StreamDispatcher<{stream.ResponseSyntax}>");
            code.Line($"public override {AsyncEnumerable}<{stream.ResponseSyntax}> Create(CaparMediator mediator, global::Capar.IStreamRequest<{stream.ResponseSyntax}> request, {Token} cancellationToken) =>");
            code.Line($"    mediator.CreateStream(({stream.Request.Syntax})request, cancellationToken);");
            code.Close();
        }

        for (int index = 0; index < plan.Notifications.Length; index++)
        {
            code.Line();
            code.Open($"private sealed class Notification{index} : NotificationDispatcher");
            code.Line($"public override {Task} Publish(CaparMediator mediator, object notification, {Token} cancellationToken) =>");
            code.Line($"    mediator.Publish(({plan.Notifications[index].Notification.Syntax})notification, cancellationToken);");
            code.Close();
        }
    }

    private static void NullCheck(CodeWriter code, string parameter, MessageShape shape)
    {
        // A struct cannot be null, and ThrowIfNull(object) would box it on every call to find that out.
        if (!shape.IsValueType)
        {
            code.Line($"global::System.ArgumentNullException.ThrowIfNull({parameter});");
        }
    }

    /// <summary>
    /// For a message type that can be derived from, sends anything that is not exactly that type through the
    /// runtime-type path.
    /// </summary>
    /// <remarks>
    /// Overload resolution picks the typed overload by the argument's static type. Without this check a
    /// <c>DerivedQuery</c> held in a <c>BaseQuery</c> variable would run <c>BaseQuery</c>'s handler, where
    /// MediatR and the interface path both go by the runtime type. For a sealed type no check is emitted.
    /// </remarks>
    private static void RuntimeTypeCheck(CodeWriter code, string parameter, MessageShape shape, string fallback)
    {
        if (shape.IsSealed)
        {
            return;
        }

        code.Line($"if ({parameter}.GetType() != typeof({shape.Type}))");
        code.Line("{");
        code.Line($"    return {fallback};");
        code.Line("}");
        code.Line();
    }

    private static string Display(string fullyQualified) =>
        Planner.Trim(fullyQualified).Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>A string builder that keeps track of indentation and braces.</summary>
    private sealed class CodeWriter
    {
        private readonly StringBuilder _builder = new();
        private int _depth;

        public void Line(string text = "")
        {
            if (text.Length > 0)
            {
                _builder.Append(' ', _depth * 4);
                _builder.Append(text);
            }

            _builder.Append('\n');
        }

        public void Open(string header)
        {
            Line(header);
            Line("{");
            _depth++;
        }

        public void Close()
        {
            _depth--;
            Line("}");
        }

        public override string ToString() => _builder.ToString();
    }
}
