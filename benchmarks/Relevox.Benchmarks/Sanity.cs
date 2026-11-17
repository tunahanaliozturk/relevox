using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace Benchmarks;

/// <summary>
/// Proves every benchmark does the work its name claims before any number is trusted.
/// </summary>
/// <remarks>
/// A behaviour registered the wrong way is simply never called, and a benchmark that skips it reports a
/// lovely number. Counting is off during measurement, so the check costs one predictable branch.
/// </remarks>
internal static class Sanity
{
    private static readonly ConcurrentDictionary<string, int> Counts = new();
    private static bool _enabled;

    public static void Count(string what)
    {
        if (_enabled)
        {
            Counts.AddOrUpdate(what, 1, static (_, count) => count + 1);
        }
    }

    public static async Task RunAsync()
    {
        _enabled = true;

        SendBenchmarks send = new();
        send.Setup();
        Expect(await send.MediatR(), 42, "MediatR send");
        Expect(await send.MediatRObject(), 42, "MediatR send(object)");
        Expect(await send.MediatorTyped(), 42, "Mediator typed send");
        Expect(await send.MediatorInterface(), 42, "Mediator ISender send");
        Expect(await send.MediatorObject(), 42, "Mediator send(object)");
        Expect(await send.RelevoxTyped(), 42, "Relevox typed send");
        Expect(await send.RelevoxInterface(), 42, "Relevox ISender send");
        Expect(await send.RelevoxObject(), 42, "Relevox send(object)");
        Expect(await send.RelevoxSingletonTyped(), 42, "Relevox singleton typed send");
        Expect(await send.RelevoxSingletonInterface(), 42, "Relevox singleton ISender send");
        Expect(await send.RelevoxSingletonObject(), 42, "Relevox singleton send(object)");
        send.Cleanup();

        ScopeBenchmarks scope = new();
        scope.Setup();
        Expect(await scope.MediatR(), 42, "MediatR send per scope");
        Expect(await scope.Relevox(), 42, "Relevox send per scope");
        Expect(await scope.Mediator(), 42, "Mediator send per scope");
        scope.Cleanup();

        TelemetryBenchmarks telemetry = new();
        telemetry.Setup();
        Expect(await telemetry.Off(), 42, "Relevox send, telemetry off");
        Expect(await telemetry.OnWithoutListener(), 42, "Relevox send, telemetry on without a listener");
        telemetry.Cleanup();

        PipelineBenchmarks pipeline = new();
        pipeline.Setup();
        Expect(await pipeline.MediatR(), 42, "MediatR pipeline send");
        Expect(await pipeline.MediatorTyped(), 42, "Mediator pipeline send");
        Expect(await pipeline.RelevoxTyped(), 42, "Relevox pipeline send");
        Expect(await pipeline.MediatRSingletonBehaviors(), 42, "MediatR pipeline send, singleton behaviours");
        Expect(await pipeline.RelevoxSingletonBehaviorsTyped(), 42, "Relevox pipeline send, singleton behaviours");
        Expect(await pipeline.RelevoxSingletonTyped(), 42, "Relevox pipeline send, singletons");
        pipeline.Cleanup();

        BuiltInBenchmarks builtIns = new();
        builtIns.Setup();
        Expect(await builtIns.MediatR(), 42, "MediatR pipeline send with built-ins");
        Expect(await builtIns.RelevoxTyped(), 42, "Relevox pipeline send with built-ins");
        builtIns.Cleanup();

        PipelineOrderBenchmarks order = new();
        order.Setup();
        Expect(await order.InOrder(), 42, "Relevox ordered pipeline send, registered in order");
        Expect(await order.Reversed(), 42, "Relevox ordered pipeline send, registered in reverse");
        order.Cleanup();

        ValidationBenchmarks validation = new();
        validation.Setup();
        Expect(await validation.Without(), 42, "Relevox send without validation");
        Expect(await validation.WithoutValidator(), 42, "Relevox send with AddValidation, no validator");
        Expect(await validation.WithValidator(), 42, "Relevox send with AddValidation and a validator");
        validation.Cleanup();

        PublishBenchmarks publish = new();
        publish.Setup();
        await publish.MediatROne();
        await publish.MediatRFive();
        await publish.MediatorOne();
        await publish.MediatorFive();
        await publish.RelevoxOne();
        await publish.RelevoxFive();
        await publish.RelevoxSingletonOne();
        await publish.RelevoxSingletonFive();
        await publish.RelevoxWhenAllOne();
        await publish.RelevoxWhenAllFive();
        publish.Cleanup();

        CheckLifetimes();

        StreamBenchmarks stream = new();
        stream.Setup();
        Expect(await stream.MediatR(), 4950, "MediatR stream");
        Expect(await stream.MediatorTyped(), 4950, "Mediator stream");
        Expect(await stream.RelevoxTyped(), 4950, "Relevox stream");
        stream.Cleanup();

        StartupBenchmarks startup = new();
        Expect(await startup.MediatR(), 42, "MediatR startup");
        Expect(await startup.Mediator(), 42, "Mediator startup");
        Expect(await startup.Relevox(), 42, "Relevox startup");

        foreach (string expected in new[] { "Mediator behaviour", "Mediator one listener", "Mediator fifth listener", "Relevox validated handler" })
        {
            Expect(Counts.GetValueOrDefault(expected), 1, expected);
        }

        foreach (string expected in new[] { "MediatR one listener", "MediatR fifth listener" })
        {
            Expect(Counts.GetValueOrDefault(expected), 1, expected);
        }

        // Three times each for Relevox: transient handlers, singleton handlers, and singletons with
        // TaskWhenAllPublisher. The fifth listener is the last one a publisher reaches, so it proves the other four
        // ran before it.
        foreach (string expected in new[] { "Relevox one listener", "Relevox fifth listener" })
        {
            Expect(Counts.GetValueOrDefault(expected), 3, expected);
        }

        // MediatR: through the plain pipeline, with the built-ins around it, and with singleton behaviours.
        Expect(Counts.GetValueOrDefault("MediatR behaviour"), 3, "MediatR behaviour");

        // Relevox, the same three, and once more with singleton handlers as well.
        Expect(Counts.GetValueOrDefault("Relevox behaviour"), 4, "Relevox behaviour");

        // Twice: the ordered behaviours registered in their order and in reverse.
        Expect(Counts.GetValueOrDefault("Relevox ordered behaviour"), 2, "Relevox ordered behaviour");

        Console.WriteLine("Every scenario reached its handler, behaviours and listeners included.");
    }

    // The rows named singletons, the scope rows and the TaskWhenAll rows only mean what they say if the containers
    // behind them were built that way. This asks the containers, built with the arguments the benchmarks use.
    private static void CheckLifetimes()
    {
        using ServiceProvider mediatr = Containers.MediatR(withPipeline: true, behaviorLifetime: ServiceLifetime.Singleton);
        ExpectSingleton<global::MediatR.IPipelineBehavior<MediatRPeer.Piped, int>>(mediatr, "MediatR singleton behaviours");

        using ServiceProvider relevox = Containers.Relevox(ServiceLifetime.Singleton, withPipeline: true, whenAll: true);
        ExpectSingleton<global::Relevox.IRequestHandler<RelevoxPeer.Ping, int>>(relevox, "Relevox singleton handler");
        ExpectSingleton<global::Relevox.IPipelineBehavior<RelevoxPeer.Piped, int>>(relevox, "Relevox singleton behaviours");
        ExpectSingleton<global::Relevox.INotificationHandler<RelevoxPeer.FiveListeners>>(relevox, "Relevox singleton listeners");
        Expect(relevox.GetRequiredService<global::Relevox.INotificationPublisher>().GetType().Name, "TaskWhenAllPublisher", "Relevox publisher");

        // Singleton behaviours with the handlers left at their transient default, the row MediatR can be compared on.
        using ServiceProvider relevoxBehaviors = Containers.Relevox(ServiceLifetime.Transient, withPipeline: true, behaviorLifetime: ServiceLifetime.Singleton);
        ExpectSingleton<global::Relevox.IPipelineBehavior<RelevoxPeer.Piped, int>>(relevoxBehaviors, "Relevox singleton behaviours, transient handlers");
        if (ReferenceEquals(relevoxBehaviors.GetRequiredService<global::Relevox.IRequestHandler<RelevoxPeer.Piped, int>>(), relevoxBehaviors.GetRequiredService<global::Relevox.IRequestHandler<RelevoxPeer.Piped, int>>()))
        {
            throw new InvalidOperationException("Relevox singleton behaviours, transient handlers: the handler is a singleton.");
        }

        // The scope rows: a mediator per scope for MediatR and Relevox, the same one for Mediator.
        using ServiceProvider mediatrScoped = Containers.MediatR(withPipeline: false);
        ExpectPerScope<global::MediatR.ISender>(mediatrScoped, expectSame: false, "MediatR mediator per scope");
        using ServiceProvider relevoxScoped = Containers.Relevox(ServiceLifetime.Singleton);
        ExpectPerScope<global::Relevox.ISender>(relevoxScoped, expectSame: false, "Relevox mediator per scope");
        using ServiceProvider mediator = Containers.Mediator(withPipeline: false);
        ExpectPerScope<global::Mediator.ISender>(mediator, expectSame: true, "Mediator mediator per scope");
    }

    private static void ExpectSingleton<T>(IServiceProvider provider, string what)
        where T : notnull
    {
        T[] first = [.. provider.GetServices<T>()];
        T[] second = [.. provider.GetServices<T>()];
        if (first.Length == 0 || first.Length != second.Length || first.Where((item, index) => !ReferenceEquals(item, second[index])).Any())
        {
            throw new InvalidOperationException($"{what}: expected the same instances on every resolve.");
        }
    }

    private static void ExpectPerScope<T>(IServiceProvider provider, bool expectSame, string what)
        where T : notnull
    {
        using IServiceScope one = provider.CreateScope();
        using IServiceScope two = provider.CreateScope();
        bool same = ReferenceEquals(one.ServiceProvider.GetRequiredService<T>(), two.ServiceProvider.GetRequiredService<T>());
        if (same != expectSame)
        {
            throw new InvalidOperationException($"{what}: expected {(expectSame ? "one instance" : "an instance")} per scope, got {(same ? "one" : "two")}.");
        }
    }

    private static void Expect(object? actual, object expected, string what)
    {
        if (!Equals(actual, expected))
        {
            throw new InvalidOperationException($"{what}: expected {expected}, got {actual ?? "null"}.");
        }
    }
}
