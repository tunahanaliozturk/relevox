using System.Collections.Concurrent;

namespace Capar.Benchmarks;

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
        send.Cleanup();

        PipelineBenchmarks pipeline = new();
        pipeline.Setup();
        Expect(await pipeline.MediatR(), 42, "MediatR pipeline send");
        Expect(await pipeline.MediatorTyped(), 42, "Mediator pipeline send");
        pipeline.Cleanup();

        PublishBenchmarks publish = new();
        publish.Setup();
        await publish.MediatROne();
        await publish.MediatRFive();
        await publish.MediatorOne();
        await publish.MediatorFive();
        publish.Cleanup();

        StreamBenchmarks stream = new();
        stream.Setup();
        Expect(await stream.MediatR(), 4950, "MediatR stream");
        Expect(await stream.MediatorTyped(), 4950, "Mediator stream");
        stream.Cleanup();

        StartupBenchmarks startup = new();
        Expect(await startup.MediatR(), 42, "MediatR startup");
        Expect(await startup.Mediator(), 42, "Mediator startup");

        foreach (string expected in new[] { "MediatR behaviour", "Mediator behaviour", "MediatR fifth listener", "Mediator fifth listener" })
        {
            Expect(Counts.GetValueOrDefault(expected), 1, expected);
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
