using System.Collections.Concurrent;

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
        send.Cleanup();

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
        publish.Cleanup();

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

        foreach (string expected in new[] { "Mediator behaviour", "MediatR fifth listener", "Mediator fifth listener", "Relevox fifth listener", "Relevox validated handler" })
        {
            Expect(Counts.GetValueOrDefault(expected), 1, expected);
        }

        // Twice each: through the plain pipeline and with the built-ins around it, and the ordered behaviours
        // registered in their order and in reverse.
        foreach (string expected in new[] { "MediatR behaviour", "Relevox behaviour", "Relevox ordered behaviour" })
        {
            Expect(Counts.GetValueOrDefault(expected), 2, expected);
        }

        Console.WriteLine("Every scenario reached its handler, behaviours and listeners included.");
    }

    private static void Expect(object? actual, object expected, string what)
    {
        if (!Equals(actual, expected))
        {
            throw new InvalidOperationException($"{what}: expected {expected}, got {actual ?? "null"}.");
        }
    }
}
