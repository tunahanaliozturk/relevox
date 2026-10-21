extern alias onrelevox;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using S = onrelevox::Scenarios;

namespace Relevox.DifferentialTests;

/// <summary>The <c>relevox.operation.duration</c> histogram, named and tagged as ADR 0013 decides.</summary>
[Collection(nameof(RunsAlone))]
public sealed class MetricsTests
{
    [Fact]
    public async Task Every_operation_records_its_duration_in_seconds_tagged_with_operation_and_message_type()
    {
        // No activity listener: the histogram does not depend on anyone tracing.
        using Measurements measurements = new();
        await using ServiceProvider provider = TelemetryTests.Build();
        ISender sender = provider.GetRequiredService<ISender>();

        await sender.Send(new S.Ping("x"));
        await sender.Send(new S.Relay("deep"));
        await provider.GetRequiredService<IPublisher>().Publish(new S.Alarm("north"));
        await sender.CreateStream(new S.Countdown(2)).ToListAsync();

        measurements.Recorded.Select(measurement => measurement.Describe()).ShouldBe(
        [
            "send Scenarios.Ping",
            "send Scenarios.Ping",
            "send Scenarios.Relay",
            "publish Scenarios.Alarm",
            "stream Scenarios.Countdown",
        ]);
        foreach (Measurement measurement in measurements.Recorded)
        {
            measurement.Instrument.ShouldBe("relevox.operation.duration");
            measurement.Unit.ShouldBe("s");
            measurement.Seconds.ShouldBeInRange(0, 60);
            measurement.Tags.Keys.ShouldBe(["relevox.operation", "relevox.message.type"], ignoreOrder: true);
        }
    }

    [Fact]
    public async Task A_failed_operation_carries_error_type()
    {
        using Measurements measurements = new();
        await using ServiceProvider provider = TelemetryTests.Build();
        ISender sender = provider.GetRequiredService<ISender>();

        Exception thrown = await TelemetryTests.Caught(() => sender.Send(new S.Explode()));
        Exception faulted = await TelemetryTests.Caught(() => sender.Send(new S.Stall()));
        Exception published = await TelemetryTests.Caught(() => provider.GetRequiredService<IPublisher>().Publish(new S.Boom()));
        Exception streamed = await TelemetryTests.Caught(() => sender.CreateStream(new S.Sputter()).ToListAsync().AsTask());

        measurements.Recorded.Select(measurement => (measurement.Describe(), measurement.Tags.GetValueOrDefault("error.type"))).ShouldBe(
        [
            ("send Scenarios.Explode", (object?)thrown.GetType().FullName),
            ("send Scenarios.Stall", faulted.GetType().FullName),
            ("publish Scenarios.Boom", published.GetType().FullName),
            ("stream Scenarios.Sputter", streamed.GetType().FullName),
        ]);
    }

    [Fact]
    public async Task A_stream_records_once_per_enumeration_and_a_runtime_only_notification_by_its_type()
    {
        using Measurements measurements = new();
        await using ServiceProvider provider = TelemetryTests.Build();
        ISender sender = provider.GetRequiredService<ISender>();

        IAsyncEnumerable<int> stream = sender.CreateStream(new S.Countdown(1));
        _ = sender.CreateStream(new S.Countdown(9));
        await stream.ToListAsync();
        await stream.ToListAsync();
        await provider.GetRequiredService<IPublisher>().Publish(S.Shipments.Reroute(7));

        measurements.Recorded.Select(measurement => measurement.Describe()).ShouldBe(
        [
            "stream Scenarios.Countdown",
            "stream Scenarios.Countdown",
            "publish Scenarios.Rerouted<System.Int32>",
        ]);
    }

    [Fact]
    public async Task Nothing_is_recorded_when_telemetry_is_off()
    {
        using Measurements measurements = new();
        await using ServiceProvider provider = TelemetryTests.Build(telemetry: false);
        ISender sender = provider.GetRequiredService<ISender>();

        await sender.Send(new S.Ping("x"));
        await TelemetryTests.Caught(() => sender.Send(new S.Explode()));
        await provider.GetRequiredService<IPublisher>().Publish(new S.Alarm("north"));
        await sender.CreateStream(new S.Countdown(2)).ToListAsync();

        measurements.Recorded.ShouldBeEmpty();
    }
}

/// <summary>One measurement, as a MeterListener saw it.</summary>
internal sealed record Measurement(string Instrument, string? Unit, double Seconds, Dictionary<string, object?> Tags)
{
    public string Describe() => $"{Tags["relevox.operation"]} {Tags["relevox.message.type"]}";
}

/// <summary>Records every measurement from the Relevox meter while it is alive.</summary>
internal sealed class Measurements : IDisposable
{
    private readonly ConcurrentQueue<Measurement> _recorded = new();
    private readonly MeterListener _listener = new();

    public Measurements()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == RelevoxTelemetry.SourceName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            _recorded.Enqueue(new Measurement(
                instrument.Name,
                instrument.Unit,
                value,
                tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value))));
        _listener.Start();
    }

    public IReadOnlyList<Measurement> Recorded => [.. _recorded];

    public void Dispose() => _listener.Dispose();
}
