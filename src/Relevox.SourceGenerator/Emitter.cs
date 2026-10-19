using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Relevox.SourceGenerator;

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

    // On every generated type: hidden from stack traces, so a failure shows the handler and the behaviours
    // around it rather than dispatch code nobody wrote, and stepped through by the debugger, so F11 on Send
    // lands in the handler.
    private const string Plumbing = "[global::System.Diagnostics.StackTraceHidden, global::System.Diagnostics.DebuggerNonUserCode, global::System.Diagnostics.DebuggerStepThrough]";

    // The same for the lambdas the generated code hands to the runtime, which compile to methods of their own.
    private const string Lambda = "[global::System.Diagnostics.StackTraceHidden, global::System.Diagnostics.DebuggerStepThrough] ";

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
        code.Line("/// The mediator Relevox generated for this assembly, from the handlers it could see when it was built.");
        code.Line("/// </summary>");
        code.Line("/// <remarks>");
        code.Line("/// Take this type as a dependency instead of <c>IMediator</c> where the call site knows what it sends:");
        code.Line("/// its typed overloads call the handler directly, with nothing to look up.");
        code.Line("/// </remarks>");
        code.Line($"[global::System.CodeDom.Compiler.GeneratedCode(\"Relevox\", \"{Version}\")]");
        code.Line(Plumbing);
        code.Open("internal sealed partial class RelevoxMediator : global::Relevox.IMediator");

        code.Line("private readonly global::System.IServiceProvider _services;");
        code.Line("private readonly global::Relevox.Internal.PipelineState<RelevoxMediator> _pipelines;");
        code.Line("private global::Relevox.INotificationPublisher? _publisher;");
        if (plan.OpenNotificationHandlers.Length > 0)
        {
            code.Line($"private static readonly global::Relevox.Internal.OpenNotificationHandlers OpenNotificationHandlers = new({string.Join(", ", plan.OpenNotificationHandlers.Select(static definition => $"typeof({definition})"))});");
        }

        foreach (KeyValuePair<string, int> handler in handlerIndex.OrderBy(static pair => pair.Value))
        {
            code.Line($"private readonly bool _keep{handler.Value};");
            code.Line($"private {handler.Key}? _handler{handler.Value};");
        }

        code.Line();
        code.Line("/// <summary>Creates the mediator over the container it resolves handlers from.</summary>");
        code.Line("/// <param name=\"services\">The container, or the scope, handlers come from.</param>");
        code.Line("/// <param name=\"keep\">");
        code.Line("/// One flag per handler, in the order <c>AddRelevox</c> registers them: true when the handler is registered");
        code.Line("/// as scoped or singleton, so the container would hand this mediator the same instance every time anyway");
        code.Line("/// and the first one can be kept.");
        code.Line("/// </param>");
        code.Line("/// <param name=\"pipelines\">Which requests this container has no behaviours for, learned on first use.</param>");
        code.Open("public RelevoxMediator(global::System.IServiceProvider services, bool[] keep, global::Relevox.Internal.PipelineState<RelevoxMediator> pipelines)");
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
        EmitInterfaceMembers(code, plan);
        EmitHandlerAccessors(code, handlerIndex);
        EmitDispatchers(code, plan);

        code.Close();
        code.Close();
        return code.ToString();
    }

    public static string Registration(EmitPlan plan)
    {
        const string TryAdd = DependencyInjection + ".Extensions.ServiceCollectionDescriptorExtensions.TryAdd";

        string mediator = $"global::{plan.Namespace}.RelevoxMediator";

        CodeWriter code = Header(plan);
        code.Open($"namespace {plan.RegistrationNamespace}");
        code.Line("/// <summary>Registers the mediator Relevox generated for this assembly.</summary>");
        code.Line($"[global::System.CodeDom.Compiler.GeneratedCode(\"Relevox\", \"{Version}\")]");
        code.Open("internal static class RelevoxServiceCollectionExtensions");
        code.Line("/// <summary>");
        code.Line("/// Registers every handler found at build time, by its concrete type, and the generated mediator as");
        code.Line("/// <c>IMediator</c>, <c>ISender</c> and <c>IPublisher</c>. Nothing is scanned at run time.");
        code.Line("/// </summary>");
        code.Line("/// <remarks>");
        code.Line("/// Calling it twice is harmless. Calling it after another project's <c>AddRelevox</c> throws, because only one");
        code.Line("/// mediator can be <c>IMediator</c> and silently keeping the first would leave this project's handlers");
        code.Line("/// unreachable.");
        code.Line("/// </remarks>");
        code.Line("/// <param name=\"services\">The service collection.</param>");
        code.Line("/// <param name=\"configure\">Adjusts the options, such as the handler lifetime.</param>");
        code.Line("/// <returns>The same service collection.</returns>");
        code.Open($"public static {DependencyInjection}.IServiceCollection AddRelevox(this {DependencyInjection}.IServiceCollection services, global::System.Action<global::Microsoft.Extensions.DependencyInjection.RelevoxOptions>? configure = null)");
        code.Line("global::System.ArgumentNullException.ThrowIfNull(services);");
        code.Open($"foreach ({DependencyInjection}.ServiceDescriptor existing in services)");
        code.Line($"if (existing.ServiceType == typeof({mediator}))");
        code.Line("{");
        code.Line("    return services;");
        code.Line("}");
        code.Line();
        // Every generated AddRelevox registers a PipelineState for its mediator, so one for another mediator means a
        // second project's AddRelevox already ran: only one mediator can be IMediator, and keeping the first
        // silently would leave this project's handlers out. An IMediator registered any other way, such as a test
        // double, is the application's own and is kept, as MediatR keeps it.
        code.Line("if (existing.ServiceType.IsGenericType");
        code.Line("    && existing.ServiceType.GetGenericTypeDefinition() == typeof(global::Relevox.Internal.PipelineState<>)");
        code.Line($"    && existing.ServiceType != typeof(global::Relevox.Internal.PipelineState<{mediator}>))");
        code.Line("{");
        code.Line("    throw new global::System.InvalidOperationException(");
        code.Line($"        \"IMediator is already registered, probably by AddRelevox from another project. Call AddRelevox once, from the project whose handlers it should reach (usually the host), and have the other projects reference Relevox.Contracts rather than Relevox. This call came from '{plan.Namespace}'.\");");
        code.Line("}");
        code.Close();
        code.Line();
        code.Line("global::Microsoft.Extensions.DependencyInjection.RelevoxOptions options = new();");
        code.Line("configure?.Invoke(options);");
        code.Line($"{DependencyInjection}.ServiceLifetime lifetime = options.Lifetime;");
        code.Line($"{DependencyInjection}.ServiceLifetime handlerLifetime = options.HandlerLifetime;");
        code.Line();

        // Handlers are added only when the application has not registered them already, as TryAdd would, but
        // against a set built once: TryAdd scans the whole collection on every call, which made startup
        // quadratic in the number of handlers.
        code.Line("global::System.Collections.Generic.HashSet<global::System.Type> registered = new();");
        code.Open($"foreach ({DependencyInjection}.ServiceDescriptor existing in services)");
        code.Line("registered.Add(existing.ServiceType);");
        code.Close();
        code.Line();

        // Each registration names its type with typeof at the call, rather than passing a Type through a
        // helper. The ServiceDescriptor constructor is annotated for trimming, and a literal typeof is what
        // lets the trimmer keep the constructor without a warning in the user's NativeAOT build.
        foreach (string handler in plan.HandlerTypes)
        {
            code.Line($"if (registered.Add(typeof({handler})))");
            code.Line("{");
            code.Line($"    services.Add(new {DependencyInjection}.ServiceDescriptor(typeof({handler}), typeof({handler}), handlerLifetime));");
            code.Line("}");
        }

        // Generic notification handlers are registered open as well, for notification types the build never saw;
        // the container closes them, as under MediatR.
        foreach (string definition in plan.OpenNotificationHandlers)
        {
            code.Line($"if (registered.Add(typeof({definition})))");
            code.Line("{");
            code.Line($"    services.Add(new {DependencyInjection}.ServiceDescriptor(typeof({definition}), typeof({definition}), handlerLifetime));");
            code.Line("}");
        }

        code.Line();
        code.Line("// Whether each handler can be kept is read from the registration actually in force, which may be one the");
        code.Line("// application made before this call. A transient handler is never kept.");
        code.Line($"global::System.Collections.Generic.Dictionary<global::System.Type, {DependencyInjection}.ServiceLifetime> lifetimes = new();");
        code.Open($"foreach ({DependencyInjection}.ServiceDescriptor descriptor in services)");
        code.Line("lifetimes[descriptor.ServiceType] = descriptor.Lifetime;");
        code.Close();
        code.Line();
        code.Line($"bool[] keep = new bool[{plan.HandlerTypes.Length}];");
        for (int index = 0; index < plan.HandlerTypes.Length; index++)
        {
            code.Line($"keep[{index}] = lifetimes.TryGetValue(typeof({plan.HandlerTypes[index]}), out {DependencyInjection}.ServiceLifetime lifetime{index}) && lifetime{index} != {DependencyInjection}.ServiceLifetime.Transient;");
        }

        code.Line();
        int requestCount = plan.Requests.Count(static request => !request.Request.IsAbstract);
        int streamCount = plan.Streams.Count(static stream => !stream.Request.IsAbstract);
        RequestEntry[] dispatched = [.. plan.Requests.Where(static request => !request.Request.IsAbstract)];

        code.Line("if (options.AutoRegisterRequestProcessors)");
        code.Line("{");
        foreach (ServiceRegistration processor in plan.Processors)
        {
            code.Line($"    {DependencyInjection}.Extensions.ServiceCollectionDescriptorExtensions.TryAddEnumerable(services, new {DependencyInjection}.ServiceDescriptor(typeof({processor.ServiceType}), typeof({processor.ImplementationType}), handlerLifetime));");
        }

        foreach (OpenRegistration processor in plan.OpenProcessors.Where(static open => !open.ServiceType.Contains("Exception")))
        {
            code.Line($"    {DependencyInjection}.Extensions.ServiceCollectionDescriptorExtensions.TryAddEnumerable(services, new {DependencyInjection}.ServiceDescriptor(typeof({processor.ServiceType}), typeof({processor.ImplementationType}), handlerLifetime));");
        }

        code.Line("}");
        code.Line();

        code.Open($"foreach ({DependencyInjection}.ServiceDescriptor processor in global::System.Linq.Enumerable.Concat(options.RequestPreProcessorsToRegister, options.RequestPostProcessorsToRegister))");
        code.Line($"{DependencyInjection}.Extensions.ServiceCollectionDescriptorExtensions.TryAddEnumerable(services, processor);");
        code.Close();
        code.Line();

        // Exception handlers and actions are registered whatever AutoRegisterRequestProcessors says, as MediatR's
        // scan registers them.
        foreach (ServiceRegistration registration in plan.ExceptionRegistrations)
        {
            code.Line($"{DependencyInjection}.Extensions.ServiceCollectionDescriptorExtensions.TryAddEnumerable(services, new {DependencyInjection}.ServiceDescriptor(typeof({registration.ServiceType}), typeof({registration.ImplementationType}), handlerLifetime));");
        }

        foreach (OpenRegistration registration in plan.OpenProcessors.Where(static open => open.ServiceType.Contains("Exception")))
        {
            code.Line($"{DependencyInjection}.Extensions.ServiceCollectionDescriptorExtensions.TryAddEnumerable(services, new {DependencyInjection}.ServiceDescriptor(typeof({registration.ServiceType}), typeof({registration.ImplementationType}), handlerLifetime));");
        }

        for (int index = 0; index < plan.ExceptionCatalogs.Length; index++)
        {
            ExceptionCatalogEntry catalog = plan.ExceptionCatalogs[index];
            code.Line($"services.Add(new {DependencyInjection}.ServiceDescriptor(typeof(global::Relevox.Internal.ExceptionCatalog<{catalog.Request.Type}, {catalog.ResponseType}>), new ExceptionCatalog{index}()));");
        }

        code.Line();

        // The built-in behaviours, in MediatR's order: the two exception behaviours first, so they sit outside
        // everything else and catch what a pre-processor throws, then the pre and post processor behaviours,
        // then whatever the options add. They are registered closed per request rather than open generic, so
        // nothing is closed at run time, with Add rather than TryAddEnumerable because each pair is new and
        // TryAddEnumerable scans the whole collection every time.
        //
        // MediatR 12 adds a processor behaviour only when a processor of that kind was added through its
        // configuration. Processors it found by scanning, or that the application registered with the container
        // itself, are registered but never run unless one was also added explicitly. That is surprising, and it
        // is matched on purpose: code moving from MediatR must not find processors running that never ran.
        code.Line("bool actionsOutside = options.RequestExceptionActionProcessorStrategy == global::Relevox.RequestExceptionActionProcessorStrategy.ApplyForUnhandledExceptions;");
        code.Line("bool exceptionHandlers = Has(services, typeof(global::Relevox.Pipeline.IRequestExceptionHandler<,,>));");
        code.Line("bool exceptionActions = Has(services, typeof(global::Relevox.Pipeline.IRequestExceptionAction<,>));");
        code.Line("bool preProcessors = options.RequestPreProcessorsToRegister.Count > 0;");
        code.Line("bool postProcessors = options.RequestPostProcessorsToRegister.Count > 0;");
        // Each built-in is registered as a slot: a singleton that keeps the behaviour's place in the container's
        // order and is replaced, or skipped, by the generated mediator. See BuiltInSlot.
        foreach ((string condition, string behavior) in new[]
        {
            ("actionsOutside && exceptionActions", "ExceptionActionSlot"),
            ("exceptionHandlers", "ExceptionHandlerSlot"),
            ("!actionsOutside && exceptionActions", "ExceptionActionSlot"),
            ("preProcessors", "PreProcessorSlot"),
            ("postProcessors", "PostProcessorSlot"),
        })
        {
            code.Line($"if ({condition})");
            code.Line("{");
            foreach (RequestEntry request in dispatched)
            {
                string response = request.ResponseType ?? "global::Relevox.Unit";
                code.Line($"    services.Add(new {DependencyInjection}.ServiceDescriptor(typeof(global::Relevox.IPipelineBehavior<{request.Request.Type}, {response}>), typeof(global::Relevox.Internal.{behavior}<{request.Request.Type}, {response}>), {DependencyInjection}.ServiceLifetime.Singleton));");
            }

            code.Line("}");
            code.Line();
        }

        code.Line("// Behaviours are resolved from the container on every send, as in MediatR, so these are simply registered.");
        code.Line("// TryAddEnumerable keeps a behaviour added twice from running twice.");
        code.Open($"foreach ({DependencyInjection}.ServiceDescriptor behavior in global::System.Linq.Enumerable.Concat(options.BehaviorsToRegister, options.StreamBehaviorsToRegister))");
        code.Line($"{DependencyInjection}.Extensions.ServiceCollectionDescriptorExtensions.TryAddEnumerable(services, behavior);");
        code.Close();
        code.Line();
        code.Line("if (options.NotificationPublisherType is not null)");
        code.Line("{");
        code.Line($"    {TryAdd}(services, new {DependencyInjection}.ServiceDescriptor(typeof(global::Relevox.INotificationPublisher), options.NotificationPublisherType, lifetime));");
        code.Line("}");
        code.Line("else");
        code.Line("{");
        code.Line($"    {TryAdd}(services, new {DependencyInjection}.ServiceDescriptor(typeof(global::Relevox.INotificationPublisher), options.NotificationPublisher));");
        code.Line("}");
        code.Line();
        code.Line("// Whether the publisher in force is the default, and whether some behaviour applies to every request, are");
        code.Line("// both settled on first use rather than here, so registrations made after this call still count.");
        code.Line("bool builtInsForEveryRequest = exceptionHandlers || exceptionActions || preProcessors || postProcessors;");
        code.Line($"services.Add(new {DependencyInjection}.ServiceDescriptor(typeof(global::Relevox.Internal.PipelineState<{mediator}>), _ => new global::Relevox.Internal.PipelineState<{mediator}>({requestCount}, {streamCount}, builtInsForEveryRequest), {DependencyInjection}.ServiceLifetime.Singleton));");
        code.Line();
        code.Line($"services.Add(new {DependencyInjection}.ServiceDescriptor(typeof({mediator}), provider => new {mediator}(provider, keep, {DependencyInjection}.ServiceProviderServiceExtensions.GetRequiredService<global::Relevox.Internal.PipelineState<{mediator}>>(provider)), lifetime));");
        foreach (string contract in new[] { "global::Relevox.IMediator", "global::Relevox.ISender", "global::Relevox.IPublisher" })
        {
            code.Line($"if (registered.Add(typeof({contract})))");
            code.Line("{");
            code.Line($"    services.Add(new {DependencyInjection}.ServiceDescriptor(typeof({contract}), static provider => {DependencyInjection}.ServiceProviderServiceExtensions.GetRequiredService<{mediator}>(provider), lifetime));");
            code.Line("}");
        }

        code.Line("return services;");
        code.Close();


        code.Line();
        code.Open($"private static bool Has({DependencyInjection}.IServiceCollection services, {Type} openContract)");
        code.Open($"foreach ({DependencyInjection}.ServiceDescriptor descriptor in services)");
        code.Line("if (descriptor.ServiceType.IsGenericType && descriptor.ServiceType.GetGenericTypeDefinition() == openContract)");
        code.Line("{");
        code.Line("    return true;");
        code.Line("}");
        code.Close();
        code.Line();
        code.Line("return false;");
        code.Close();



        EmitExceptionCatalogs(code, plan);


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

    private static void EmitExceptionCatalogs(CodeWriter code, EmitPlan plan)
    {
        for (int index = 0; index < plan.ExceptionCatalogs.Length; index++)
        {
            ExceptionCatalogEntry catalog = plan.ExceptionCatalogs[index];
            string request = catalog.Request.Syntax;
            string response = catalog.ResponseSyntax;

            code.Line();
            code.Line(Plumbing);
            code.Open($"private sealed class ExceptionCatalog{index} : global::Relevox.Internal.ExceptionCatalog<{request}, {response}>");
            code.Line($"public override bool HasHandlers => {(catalog.HandledExceptions.Length > 0 ? "true" : "false")};");
            code.Line();
            code.Line($"public override bool HasActions => {(catalog.ActionExceptions.Length > 0 ? "true" : "false")};");

            foreach ((string method, string parameters, string arguments, string step, EquatableArray<string> types) in new[]
            {
                ("Handle", $"global::System.IServiceProvider services, {request} request, global::System.Exception exception, global::Relevox.Pipeline.RequestExceptionHandlerState<{response}> state, {Token} cancellationToken", "services, request, exception, state, seen, cancellationToken", "HandleAs", catalog.HandledExceptions),
                ("Execute", $"global::System.IServiceProvider services, {request} request, global::System.Exception exception, {Token} cancellationToken", "services, request, exception, seen, cancellationToken", "ExecuteAs", catalog.ActionExceptions),
            })
            {
                code.Line();
                if (types.Length == 0)
                {
                    code.Line($"public override {Task} {method}({parameters}) => {Task}.CompletedTask;");
                    continue;
                }

                // Most specific exception type first, as MediatR walks it. The base type of Exception is object,
                // so the walk ends there.
                code.Open($"public override async {Task} {method}({parameters})");
                code.Line("global::System.ArgumentNullException.ThrowIfNull(exception);");
                code.Line("global::System.Collections.Generic.HashSet<global::System.Type> seen = new();");
                code.Open($"for ({Type}? type = exception.GetType(); type is not null; type = type.BaseType)");
                foreach (string exceptionType in types)
                {
                    code.Line($"if (type == typeof({exceptionType}))");
                    code.Line("{");
                    code.Line(step == "HandleAs"
                        ? $"    if (await {step}<{exceptionType}>({arguments}).ConfigureAwait(false))"
                        : $"    await {step}<{exceptionType}>({arguments}).ConfigureAwait(false);");
                    if (step == "HandleAs")
                    {
                        code.Line("    {");
                        code.Line("        return;");
                        code.Line("    }");
                    }

                    code.Line("}");
                }

                code.Close();
                code.Close();
            }

            code.Close();
        }
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
                ? $"global::Relevox.IRequestHandler<{request.Request.Syntax}, {request.ResponseSyntax}>"
                : $"global::Relevox.IRequestHandler<{request.Request.Syntax}>";

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
                    ? $"Send<{request.ResponseSyntax}>((global::Relevox.IRequest<{request.ResponseSyntax}>)request, cancellationToken)"
                    : "Send<global::Relevox.IRequest>(request, cancellationToken)");
            string response = request.ResponseSyntax ?? "global::Relevox.Unit";
            string direct = $"(({contract})Handler{handler}()).Handle(request, cancellationToken)";
            string faulted = hasResponse ? $"{Task}.FromException<{request.ResponseSyntax}>(exception)" : $"{Task}.FromException(exception)";

            // The handler is resolved inside the innermost step, so a behaviour that short-circuits never causes
            // it to be constructed, as in MediatR. A request without a response runs through behaviours typed
            // over Unit, which is how MediatR 12 models it too.
            string pipeline = hasResponse
                ? $"global::Relevox.Internal.RequestPipeline.Run(this, request, behaviors, {Lambda}static (mediator, message, token) => (({contract})mediator.Handler{handler}()).Handle(message, token), cancellationToken)"
                : $"global::Relevox.Internal.RequestPipeline.Run(this, request, behaviors, {Lambda}static async (mediator, message, token) => {{ await (({contract})mediator.Handler{handler}()).Handle(message, token).ConfigureAwait(false); return global::Relevox.Unit.Value; }}, cancellationToken)";

            // The send itself is kept to a byte read and a call so the JIT can inline it: a method with an
            // exception handler is never inlined, and the M2 benchmark showed the cost (a singleton send went
            // from 0.46 ns to 1.9 ns). Everything else is in a helper that the direct path never reaches.
            code.Line($"if (_pipelines.TryDirect({position}, out bool asynchronous) && !asynchronous)");
            code.Line("{");
            code.Line($"    return {direct};");
            code.Line("}");
            code.Line();
            code.Line($"return SendThroughPipeline{position}(request, cancellationToken);");
            code.Close();

            code.Line();
            code.Open($"private {returns} SendThroughPipeline{position}({request.Request.Syntax} request, {Token} cancellationToken)");
            code.Line($"global::Relevox.IPipelineBehavior<{request.Request.Syntax}, {response}>[]? behaviors = null;");
            code.Line($"if (!_pipelines.TryDirect({position}, out bool asynchronous))");
            code.Line("{");
            code.Line($"    behaviors = _pipelines.Behaviors<{request.Request.Syntax}, {response}>({position}, _services, out asynchronous);");
            code.Line("}");
            code.Line();
            code.Line("if (!asynchronous)");
            code.Line("{");
            code.Line($"    return behaviors is null ? {direct} : {pipeline};");
            code.Line("}");
            code.Line();
            code.Line("// MediatR's built-in behaviours would be outermost here, and async, so a throw becomes a faulted task.");
            code.Line("try");
            code.Line("{");
            code.Line($"    return behaviors is null ? {direct} : {pipeline};");
            code.Line("}");
            code.Line("catch (global::System.Exception exception)");
            code.Line("{");
            code.Line($"    return {faulted};");
            code.Line("}");
            code.Close();
        }
    }

    private static void EmitStreams(CodeWriter code, EmitPlan plan, Dictionary<string, int> handlerIndex)
    {
        int position = -1;
        for (int index = 0; index < plan.Streams.Length; index++)
        {
            StreamEntry stream = plan.Streams[index];
            if (stream.Request.IsAbstract)
            {
                continue;
            }

            position++;

            int handler = handlerIndex[stream.HandlerType];
            code.Line();
            code.Line($"/// <summary>Creates the stream for <c>{Display(stream.Request.Type)}</c> from <c>{Display(stream.HandlerType)}</c>.</summary>");
            code.Line("/// <param name=\"request\">The request.</param>");
            code.Line("/// <param name=\"cancellationToken\">Cancels the enumeration.</param>");
            code.Line("/// <returns>The stream. The handler is resolved and called when enumeration starts.</returns>");
            code.Open($"public {AsyncEnumerable}<{stream.ResponseSyntax}> CreateStream({stream.Request.Syntax} request, {Token} cancellationToken = default)");
            NullCheck(code, "request", stream.Request);
            RuntimeTypeCheck(code, "request", stream.Request, $"CreateStream<{stream.ResponseSyntax}>((global::Relevox.IStreamRequest<{stream.ResponseSyntax}>)request, cancellationToken)");
            code.Line($"return new global::Relevox.Internal.DeferredEnumerable<RelevoxMediator, {stream.Request.Syntax}, {stream.ResponseSyntax}>(this, request, {Lambda}static (mediator, message, token) => mediator.OpenStream{index}(message, token), cancellationToken);");
            code.Close();

            // Called on the first MoveNextAsync, with the combined token: the handler is resolved when enumeration
            // starts, as in MediatR, and its stream is then enumerated directly, without a wrapper per item.
            code.Line();
            code.Open($"private {AsyncEnumerable}<{stream.ResponseSyntax}> OpenStream{index}({stream.Request.Syntax} request, {Token} cancellationToken)");
            string contract = $"global::Relevox.IStreamRequestHandler<{stream.Request.Syntax}, {stream.ResponseSyntax}>";
            code.Line($"global::Relevox.IStreamPipelineBehavior<{stream.Request.Syntax}, {stream.ResponseSyntax}>[]? behaviors = _pipelines.StreamBehaviors<{stream.Request.Syntax}, {stream.ResponseSyntax}>({position}, _services);");
            code.Line("return behaviors is null");
            code.Line($"    ? (({contract})Handler{handler}()).Handle(request, cancellationToken)");
            code.Line($"    : global::Relevox.Internal.StreamPipeline.Run(this, request, behaviors, {Lambda}static (mediator, message, token) => (({contract})mediator.Handler{handler}()).Handle(message, token), cancellationToken);");
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
                // A configured publisher is still called with no handlers, as MediatR calls it.
                code.Line($"return _pipelines.SequentialPublish(_services) ? {Task}.CompletedTask : PublishEmpty(notification, cancellationToken);");
                code.Close();
                continue;
            }

            // Every handler is resolved before the first one runs, as MediatR does, so a handler whose
            // construction fails throws out of Publish rather than after earlier handlers have already run.
            List<string> arguments = ["notification"];
            for (int target = 0; target < notification.Targets.Length; target++)
            {
                NotificationTarget handler = notification.Targets[target];
                code.Line($"global::Relevox.INotificationHandler<{handler.HandledSyntax}> handler{target} = Handler{handlerIndex[handler.HandlerType]}();");
                arguments.Add($"handler{target}");
            }

            arguments.Add("cancellationToken");
            code.Line("return _pipelines.SequentialPublish(_services)");
            code.Line($"    ? PublishSequentially{index}({string.Join(", ", arguments)})");
            code.Line($"    : PublishThrough{index}({string.Join(", ", arguments)});");
            code.Close();

            // A configured publisher gets one executor per handler, in the same order, as MediatR builds them.
            List<string> throughParameters = [$"{shape.Syntax} notification"];
            for (int target = 0; target < notification.Targets.Length; target++)
            {
                throughParameters.Add($"global::Relevox.INotificationHandler<{notification.Targets[target].HandledSyntax}> handler{target}");
            }

            throughParameters.Add($"{Token} cancellationToken");
            code.Line();
            code.Open($"private {Task} PublishThrough{index}({string.Join(", ", throughParameters)})");
            // An array initializer rather than a collection expression: the generated code compiles as part of the
            // user's project, which may pin C# 11.
            code.Line("global::Relevox.NotificationHandlerExecutor[] executors = new global::Relevox.NotificationHandlerExecutor[]");
            code.Line("{");
            for (int target = 0; target < notification.Targets.Length; target++)
            {
                code.Line($"    new(handler{target}, (message, token) => handler{target}.Handle(({shape.Syntax})message, token)),");
            }

            code.Line("};");
            code.Line("return Publisher().Publish(executors, notification, cancellationToken);");
            code.Close();

            // Async even for one handler, so an exception a handler throws before its first await ends up in
            // the returned task rather than escaping from Publish, as with MediatR's default publisher. When
            // every handler completes synchronously this allocates nothing: the builder returns a cached task.
            List<string> parameters = [$"{shape.Syntax} notification"];
            for (int target = 0; target < notification.Targets.Length; target++)
            {
                parameters.Add($"global::Relevox.INotificationHandler<{notification.Targets[target].HandledSyntax}> handler{target}");
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

    private static void EmitInterfaceMembers(CodeWriter code, EmitPlan plan)
    {
        code.Line();
        code.Line("/// <inheritdoc />");
        code.Open($"public {Task}<TResponse> Send<TResponse>(global::Relevox.IRequest<TResponse> request, {Token} cancellationToken = default)");
        code.Line("global::System.ArgumentNullException.ThrowIfNull(request);");
        code.Line($"{Type} type = request.GetType();");
        code.Line("if (Requests.TryGetValue(type, out RequestDispatcher? dispatcher) && dispatcher is RequestDispatcher<TResponse> typed)");
        code.Line("{");
        code.Line("    return typed.Send(this, request, cancellationToken);");
        code.Line("}");
        code.Line();
        code.Line("// With a behaviour that applies to every request, MediatR runs a pipeline before finding no handler,");
        code.Line("// so the task faults rather than Send throwing.");
        code.Line("return _pipelines.EveryRequestHasBehaviours(_services)");
        code.Line($"    ? {Task}.FromException<TResponse>(NoHandler(type))");
        code.Line("    : throw NoHandler(type);");
        code.Close();

        code.Line();
        code.Line("/// <inheritdoc />");
        code.Line($"public {Task} Send<TRequest>(TRequest request, {Token} cancellationToken = default)");
        code.Open("    where TRequest : global::Relevox.IRequest");
        code.Line("if (request is null)");
        code.Line("{");
        code.Line("    throw new global::System.ArgumentNullException(nameof(request));");
        code.Line("}");
        code.Line();
        code.Line($"{Type} type = request.GetType();");
        code.Line("if (Requests.TryGetValue(type, out RequestDispatcher? dispatcher) && dispatcher is VoidRequestDispatcher typed)");
        code.Line("{");
        code.Line("    return typed.Send(this, request, cancellationToken);");
        code.Line("}");
        code.Line();
        code.Line("return _pipelines.EveryRequestHasBehaviours(_services)");
        code.Line($"    ? {Task}.FromException(NoHandler(type))");
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
        code.Line("return request is global::Relevox.IBaseRequest");
        code.Line($"    ? {Task}.FromException<object?>(NoHandler(type))");
        code.Line("    : throw new global::System.ArgumentException($\"'{type}' does not implement IRequest or IRequest<TResponse>.\", nameof(request));");
        code.Close();

        code.Line();
        code.Line("/// <inheritdoc />");
        code.Open($"public {AsyncEnumerable}<TResponse> CreateStream<TResponse>(global::Relevox.IStreamRequest<TResponse> request, {Token} cancellationToken = default)");
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
        code.Line("if (contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(global::Relevox.IStreamRequest<>))");
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
        code.Line("if (notification is not global::Relevox.INotification known)");
        code.Line("{");
        code.Line("    throw new global::System.ArgumentException($\"'{notification.GetType()}' does not implement INotification.\", nameof(notification));");
        code.Line("}");
        code.Line();
        code.Line(Unlisted(plan, "known"));
        code.Close();

        code.Line();
        code.Line("/// <inheritdoc />");
        code.Line($"public {Task} Publish<TNotification>(TNotification notification, {Token} cancellationToken = default)");
        code.Open("    where TNotification : global::Relevox.INotification");
        code.Line("if (notification is null)");
        code.Line("{");
        code.Line("    throw new global::System.ArgumentNullException(nameof(notification));");
        code.Line("}");
        code.Line();
        code.Line("if (Notifications.TryGetValue(notification.GetType(), out NotificationDispatcher? dispatcher))");
        code.Line("{");
        code.Line("    return dispatcher.Publish(this, notification, cancellationToken);");
        code.Line("}");
        code.Line();
        code.Line(Unlisted(plan, "notification"));
        code.Close();
    }

    // A notification type with no entry: nothing written handles it, but a generic handler may still close over it.
    private static string Unlisted(EmitPlan plan, string notification) =>
        plan.OpenNotificationHandlers.Length > 0
            ? $"return OpenNotificationHandlers.Publish(_services, {notification}, _pipelines.SequentialPublish(_services) ? null : Publisher(), cancellationToken);"
            : $"return _pipelines.SequentialPublish(_services) ? {Task}.CompletedTask : PublishEmpty({notification}, cancellationToken);";

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
        code.Line("private global::Relevox.INotificationPublisher Publisher() => _publisher ??= Resolve<global::Relevox.INotificationPublisher>();");
        code.Line();
        code.Line($"private {Task} PublishEmpty(global::Relevox.INotification notification, {Token} cancellationToken) =>");
        code.Line("    Publisher().Publish(global::System.Array.Empty<global::Relevox.NotificationHandlerExecutor>(), notification, cancellationToken);");
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
        code.Line("    new($\"No handler for '{type}' was found when this assembly was built. Relevox finds handlers at compile time, so the handler has to be in this project or in a project it references.\");");
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
        code.Line(Plumbing);
        code.Open("private abstract class RequestDispatcher");
        code.Line($"public abstract {Task}<object?> SendObject(RelevoxMediator mediator, object request, {Token} cancellationToken);");
        code.Close();

        code.Line();
        code.Line(Plumbing);
        code.Open("private abstract class RequestDispatcher<TResponse> : RequestDispatcher");
        code.Line($"public abstract {Task}<TResponse> Send(RelevoxMediator mediator, global::Relevox.IRequest<TResponse> request, {Token} cancellationToken);");
        code.Line();
        code.Line($"public sealed override async {Task}<object?> SendObject(RelevoxMediator mediator, object request, {Token} cancellationToken) =>");
        code.Line("    await Send(mediator, (global::Relevox.IRequest<TResponse>)request, cancellationToken).ConfigureAwait(false);");
        code.Close();

        code.Line();
        code.Line(Plumbing);
        code.Open("private abstract class VoidRequestDispatcher : RequestDispatcher");
        code.Line($"public abstract {Task} Send(RelevoxMediator mediator, global::Relevox.IRequest request, {Token} cancellationToken);");
        code.Line();
        code.Open($"public sealed override async {Task}<object?> SendObject(RelevoxMediator mediator, object request, {Token} cancellationToken)");
        code.Line("await Send(mediator, (global::Relevox.IRequest)request, cancellationToken).ConfigureAwait(false);");
        code.Line();
        code.Line("// MediatR 12 answers a request without a response with a boxed Unit here, not null.");
        code.Line("return global::Relevox.Unit.Value;");
        code.Close();
        code.Close();

        code.Line();
        code.Line(Plumbing);
        code.Open("private abstract class StreamDispatcher");
        code.Line($"public abstract {AsyncEnumerable}<object?> CreateObject(RelevoxMediator mediator, object request, {Token} cancellationToken);");
        code.Close();

        code.Line();
        code.Line(Plumbing);
        code.Open("private abstract class StreamDispatcher<TResponse> : StreamDispatcher");
        code.Line($"public abstract {AsyncEnumerable}<TResponse> Create(RelevoxMediator mediator, global::Relevox.IStreamRequest<TResponse> request, {Token} cancellationToken);");
        code.Line();
        // The boxing iterator already combines the creation token with any WithCancellation token, so the inner
        // stream is created without one and receives the combined token on enumeration. Passing it twice made
        // the inner stream link the same two tokens a second time.
        code.Line($"public sealed override {AsyncEnumerable}<object?> CreateObject(RelevoxMediator mediator, object request, {Token} cancellationToken) =>");
        code.Line("    Box(Create(mediator, (global::Relevox.IStreamRequest<TResponse>)request, default), cancellationToken);");
        code.Line();
        code.Open($"private static async {AsyncEnumerable}<object?> Box({AsyncEnumerable}<TResponse> items, [global::System.Runtime.CompilerServices.EnumeratorCancellation] {Token} cancellationToken)");
        code.Open("await foreach (TResponse item in global::System.Threading.Tasks.TaskAsyncEnumerableExtensions.WithCancellation(items, cancellationToken).ConfigureAwait(false))");
        code.Line("yield return item;");
        code.Close();
        code.Close();
        code.Close();

        code.Line();
        code.Line(Plumbing);
        code.Open("private abstract class NotificationDispatcher");
        code.Line($"public abstract {Task} Publish(RelevoxMediator mediator, object notification, {Token} cancellationToken);");
        code.Close();

        for (int index = 0; index < requests.Length; index++)
        {
            RequestEntry request = requests[index];
            code.Line();
            if (request.ResponseSyntax is null)
            {
                code.Line(Plumbing);
                code.Open($"private sealed class Request{index} : VoidRequestDispatcher");
                code.Line($"public override {Task} Send(RelevoxMediator mediator, global::Relevox.IRequest request, {Token} cancellationToken) =>");
            }
            else
            {
                code.Line(Plumbing);
                code.Open($"private sealed class Request{index} : RequestDispatcher<{request.ResponseSyntax}>");
                code.Line($"public override {Task}<{request.ResponseSyntax}> Send(RelevoxMediator mediator, global::Relevox.IRequest<{request.ResponseSyntax}> request, {Token} cancellationToken) =>");
            }

            code.Line($"    mediator.Send(({request.Request.Syntax})request, cancellationToken);");
            code.Close();
        }

        foreach ((StreamEntry stream, int index) in streams)
        {
            code.Line();
            code.Line(Plumbing);
            code.Open($"private sealed class StreamDispatcher{index} : StreamDispatcher<{stream.ResponseSyntax}>");
            code.Line($"public override {AsyncEnumerable}<{stream.ResponseSyntax}> Create(RelevoxMediator mediator, global::Relevox.IStreamRequest<{stream.ResponseSyntax}> request, {Token} cancellationToken) =>");
            code.Line($"    mediator.CreateStream(({stream.Request.Syntax})request, cancellationToken);");
            code.Close();
        }

        for (int index = 0; index < plan.Notifications.Length; index++)
        {
            code.Line();
            code.Line(Plumbing);
            code.Open($"private sealed class Notification{index} : NotificationDispatcher");
            code.Line($"public override {Task} Publish(RelevoxMediator mediator, object notification, {Token} cancellationToken) =>");
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
