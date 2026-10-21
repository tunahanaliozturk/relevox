using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Relevox.Benchmarks;

namespace Benchmarks;

// Benchmarks return the library's own Task or ValueTask instead of awaiting it in an async method. An
// async wrapper would add a state machine to every row and measure that instead of the mediator.
//
// Mediator and Relevox both have two send paths: the generated concrete class, whose overloads the compiler
// binds by request type, and ISender, for callers that only hold the interface. Both are measured because
// real code uses both. Relevox is measured with its default transient handlers and with singletons, because
// Mediator's default is singletons and that choice, more than dispatch, decides its numbers.

/// <summary>Request/response with no behaviours: the cost of getting a call to its handler.</summary>
[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "Median", "RatioSD")]
public class SendBenchmarks
{
    private readonly MediatRPeer.Ping _mediatrPing = new(1);
    private readonly MediatorPeer.Ping _mediatorPing = new(1);
    private ServiceProvider _mediatr = null!;
    private ServiceProvider _mediator = null!;
    private IServiceScope _mediatrScope = null!;
    private IServiceScope _mediatorScope = null!;
    private global::MediatR.ISender _mediatrSender = null!;
    private global::Mediator.Mediator _mediatorTyped = null!;
    private global::Mediator.ISender _mediatorSender = null!;
    private readonly RelevoxPeer.Ping _relevoxPing = new(1);
    private ServiceProvider _relevox = null!;
    private ServiceProvider _relevoxSingleton = null!;
    private IServiceScope _relevoxScope = null!;
    private RelevoxMediator _relevoxTyped = null!;
    private global::Relevox.ISender _relevoxSender = null!;
    private RelevoxMediator _relevoxSingletonTyped = null!;
    private global::Relevox.ISender _relevoxSingletonSender = null!;

    [GlobalSetup]
    public void Setup()
    {
        _mediatr = Containers.MediatR(withPipeline: false);
        _mediator = Containers.Mediator(withPipeline: false);
        _relevox = Containers.Relevox(ServiceLifetime.Transient);
        _relevoxSingleton = Containers.Relevox(ServiceLifetime.Singleton);
        _relevoxScope = _relevox.CreateScope();
        _relevoxTyped = _relevoxScope.ServiceProvider.GetRequiredService<RelevoxMediator>();
        _relevoxSender = _relevoxScope.ServiceProvider.GetRequiredService<global::Relevox.ISender>();
        _relevoxSingletonTyped = _relevoxSingleton.GetRequiredService<RelevoxMediator>();
        _relevoxSingletonSender = _relevoxSingleton.GetRequiredService<global::Relevox.ISender>();

        // One scope for the whole run. Creating a scope per call would put the container's own allocation
        // into every row and drown the difference being measured.
        _mediatrScope = _mediatr.CreateScope();
        _mediatorScope = _mediator.CreateScope();
        _mediatrSender = _mediatrScope.ServiceProvider.GetRequiredService<global::MediatR.ISender>();
        _mediatorTyped = _mediatorScope.ServiceProvider.GetRequiredService<global::Mediator.Mediator>();
        _mediatorSender = _mediatorScope.ServiceProvider.GetRequiredService<global::Mediator.ISender>();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _mediatrScope.Dispose();
        _mediatorScope.Dispose();
        _mediatr.Dispose();
        _mediator.Dispose();
        _relevoxScope.Dispose();
        _relevox.Dispose();
        _relevoxSingleton.Dispose();
    }

    [Benchmark(Baseline = true, Description = "MediatR 12.5")]
    public Task<int> MediatR() => _mediatrSender.Send(_mediatrPing);

    [Benchmark(Description = "Relevox, typed")]
    public Task<int> RelevoxTyped() => _relevoxTyped.Send(_relevoxPing);

    [Benchmark(Description = "Relevox, ISender")]
    public Task<int> RelevoxInterface() => _relevoxSender.Send(_relevoxPing);

    [Benchmark(Description = "Relevox, object")]
    public Task<object?> RelevoxObject() => _relevoxSender.Send((object)_relevoxPing);

    [Benchmark(Description = "Relevox singletons, typed")]
    public Task<int> RelevoxSingletonTyped() => _relevoxSingletonTyped.Send(_relevoxPing);

    [Benchmark(Description = "Relevox singletons, ISender")]
    public Task<int> RelevoxSingletonInterface() => _relevoxSingletonSender.Send(_relevoxPing);

    [Benchmark(Description = "MediatR 12.5, object")]
    public Task<object?> MediatRObject() => _mediatrSender.Send((object)_mediatrPing);

    [Benchmark(Description = "Mediator 3.0, typed")]
    public ValueTask<int> MediatorTyped() => _mediatorTyped.Send(_mediatorPing);

    [Benchmark(Description = "Mediator 3.0, ISender")]
    public ValueTask<int> MediatorInterface() => _mediatorSender.Send<int>(_mediatorPing);

    [Benchmark(Description = "Mediator 3.0, object")]
    public ValueTask<object?> MediatorObject() => _mediatorSender.Send((object)_mediatorPing);
}

/// <summary>
/// What telemetry costs a send: off, the generated mediator reads one flag; on with nothing listening, it also
/// asks the activity source and the duration histogram whether anyone listens (ADR 0013); on with a listener, it
/// creates and stops a span and records a measurement, as an application exporting them would. Singleton
/// handlers, so the dispatch is all there is to measure.
/// </summary>
/// <remarks>
/// BenchmarkDotNet runs each case in a process of its own, so the listener the last case adds is never there for
/// the others.
/// </remarks>
[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "Median", "RatioSD")]
public class TelemetryBenchmarks
{
    private readonly RelevoxPeer.Ping _ping = new(1);
    private ServiceProvider _off = null!;
    private ServiceProvider _on = null!;
    private RelevoxMediator _offTyped = null!;
    private RelevoxMediator _onTyped = null!;
    // Static: there is one process per case, and listeners are process-wide anyway.
    private static System.Diagnostics.ActivityListener? s_spans;
    private static System.Diagnostics.Metrics.MeterListener? s_measurements;

    [GlobalSetup(Targets = [nameof(Off), nameof(OnWithoutListener)])]
    public void Setup()
    {
        _off = Containers.Relevox(ServiceLifetime.Singleton);
        _on = Containers.Relevox(ServiceLifetime.Singleton, telemetry: true);
        _offTyped = _off.GetRequiredService<RelevoxMediator>();
        _onTyped = _on.GetRequiredService<RelevoxMediator>();
    }

    // Records every span and measurement and throws them away, which is what an exporter costs the caller before
    // it batches anything.
    [GlobalSetup(Target = nameof(OnWithListener))]
    public void SetupWithListener()
    {
        Setup();
        s_spans = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = static source => source.Name == global::Relevox.RelevoxTelemetry.SourceName,
            Sample = static (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
        };
        System.Diagnostics.ActivitySource.AddActivityListener(s_spans);
        s_measurements = new System.Diagnostics.Metrics.MeterListener
        {
            InstrumentPublished = static (instrument, listener) =>
            {
                if (instrument.Meter.Name == global::Relevox.RelevoxTelemetry.SourceName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        s_measurements.SetMeasurementEventCallback<double>(static (_, _, _, _) => { });
        s_measurements.Start();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        s_spans?.Dispose();
        s_measurements?.Dispose();
        _off.Dispose();
        _on.Dispose();
    }

    [Benchmark(Baseline = true, Description = "Relevox singletons, typed, telemetry off")]
    public Task<int> Off() => _offTyped.Send(_ping);

    [Benchmark(Description = "Relevox singletons, typed, telemetry on, no listener")]
    public Task<int> OnWithoutListener() => _onTyped.Send(_ping);

    [Benchmark(Description = "Relevox singletons, typed, telemetry on, listening")]
    public Task<int> OnWithListener() => _onTyped.Send(_ping);
}

/// <summary>The same send with three behaviours in front of the handler.</summary>
[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "Median", "RatioSD")]
public class PipelineBenchmarks
{
    private readonly MediatRPeer.Piped _mediatrPiped = new(1);
    private readonly MediatorPeer.Piped _mediatorPiped = new(1);
    private ServiceProvider _mediatr = null!;
    private ServiceProvider _mediator = null!;
    private IServiceScope _mediatrScope = null!;
    private IServiceScope _mediatorScope = null!;
    private global::MediatR.ISender _mediatrSender = null!;
    private global::Mediator.Mediator _mediatorTyped = null!;
    private readonly RelevoxPeer.Piped _relevoxPiped = new(1);
    private ServiceProvider _relevox = null!;
    private IServiceScope _relevoxScope = null!;
    private RelevoxMediator _relevoxTyped = null!;

    [GlobalSetup]
    public void Setup()
    {
        _mediatr = Containers.MediatR(withPipeline: true);
        _mediator = Containers.Mediator(withPipeline: true);
        _relevox = Containers.Relevox(ServiceLifetime.Transient, withPipeline: true);
        _relevoxScope = _relevox.CreateScope();
        _relevoxTyped = _relevoxScope.ServiceProvider.GetRequiredService<RelevoxMediator>();
        _mediatrScope = _mediatr.CreateScope();
        _mediatorScope = _mediator.CreateScope();
        _mediatrSender = _mediatrScope.ServiceProvider.GetRequiredService<global::MediatR.ISender>();
        _mediatorTyped = _mediatorScope.ServiceProvider.GetRequiredService<global::Mediator.Mediator>();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _mediatrScope.Dispose();
        _mediatorScope.Dispose();
        _mediatr.Dispose();
        _mediator.Dispose();
        _relevoxScope.Dispose();
        _relevox.Dispose();
    }

    [Benchmark(Baseline = true, Description = "MediatR 12.5")]
    public Task<int> MediatR() => _mediatrSender.Send(_mediatrPiped);

    [Benchmark(Description = "Relevox, typed")]
    public Task<int> RelevoxTyped() => _relevoxTyped.Send(_relevoxPiped);

    [Benchmark(Description = "Mediator 3.0, typed")]
    public ValueTask<int> MediatorTyped() => _mediatorTyped.Send(_mediatorPiped);
}

/// <summary>
/// The pipeline send again, in an application that also configures a pre- and a post-processor for a different
/// request.
/// </summary>
/// <remarks>
/// Both libraries then add their built-in processor behaviours for every request, so Piped carries two
/// behaviours with nothing to do on top of its three. This is the shape of most real applications: a
/// validation or logging processor for some requests, behaviours for others.
/// </remarks>
[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "Median", "RatioSD")]
public class BuiltInBenchmarks
{
    private readonly MediatRPeer.Piped _mediatrPiped = new(1);
    private readonly RelevoxPeer.Piped _relevoxPiped = new(1);
    private ServiceProvider _mediatr = null!;
    private ServiceProvider _relevox = null!;
    private IServiceScope _mediatrScope = null!;
    private IServiceScope _relevoxScope = null!;
    private global::MediatR.ISender _mediatrSender = null!;
    private RelevoxMediator _relevoxTyped = null!;

    [GlobalSetup]
    public void Setup()
    {
        _mediatr = Containers.MediatR(withPipeline: true, withProcessors: true);
        _relevox = Containers.Relevox(ServiceLifetime.Transient, withPipeline: true, withProcessors: true);
        _mediatrScope = _mediatr.CreateScope();
        _relevoxScope = _relevox.CreateScope();
        _mediatrSender = _mediatrScope.ServiceProvider.GetRequiredService<global::MediatR.ISender>();
        _relevoxTyped = _relevoxScope.ServiceProvider.GetRequiredService<RelevoxMediator>();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _mediatrScope.Dispose();
        _relevoxScope.Dispose();
        _mediatr.Dispose();
        _relevox.Dispose();
    }

    [Benchmark(Baseline = true, Description = "MediatR 12.5")]
    public Task<int> MediatR() => _mediatrSender.Send(_mediatrPiped);

    [Benchmark(Description = "Relevox, typed")]
    public Task<int> RelevoxTyped() => _relevoxTyped.Send(_relevoxPiped);
}

/// <summary>Notifications to one and to five handlers, with each library's default publisher.</summary>
[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "Median", "RatioSD")]
public class PublishBenchmarks
{
    private readonly MediatRPeer.OneListener _mediatrOne = new();
    private readonly MediatRPeer.FiveListeners _mediatrFive = new();
    private readonly MediatorPeer.OneListener _mediatorOne = new();
    private readonly MediatorPeer.FiveListeners _mediatorFive = new();
    private ServiceProvider _mediatr = null!;
    private ServiceProvider _mediator = null!;
    private IServiceScope _mediatrScope = null!;
    private IServiceScope _mediatorScope = null!;
    private global::MediatR.IPublisher _mediatrPublisher = null!;
    private global::Mediator.Mediator _mediatorTyped = null!;
    private readonly RelevoxPeer.OneListener _relevoxOne = new();
    private readonly RelevoxPeer.FiveListeners _relevoxFive = new();
    private ServiceProvider _relevox = null!;
    private IServiceScope _relevoxScope = null!;
    private RelevoxMediator _relevoxTyped = null!;

    [GlobalSetup]
    public void Setup()
    {
        _mediatr = Containers.MediatR(withPipeline: false);
        _mediator = Containers.Mediator(withPipeline: false);
        _relevox = Containers.Relevox(ServiceLifetime.Transient);
        _relevoxScope = _relevox.CreateScope();
        _relevoxTyped = _relevoxScope.ServiceProvider.GetRequiredService<RelevoxMediator>();
        _mediatrScope = _mediatr.CreateScope();
        _mediatorScope = _mediator.CreateScope();
        _mediatrPublisher = _mediatrScope.ServiceProvider.GetRequiredService<global::MediatR.IPublisher>();
        _mediatorTyped = _mediatorScope.ServiceProvider.GetRequiredService<global::Mediator.Mediator>();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _mediatrScope.Dispose();
        _mediatorScope.Dispose();
        _mediatr.Dispose();
        _mediator.Dispose();
        _relevoxScope.Dispose();
        _relevox.Dispose();
    }

    [Benchmark(Baseline = true, Description = "MediatR 12.5, 1 handler")]
    public Task MediatROne() => _mediatrPublisher.Publish(_mediatrOne);

    [Benchmark(Description = "Relevox, 1 handler")]
    public Task RelevoxOne() => _relevoxTyped.Publish(_relevoxOne);

    [Benchmark(Description = "Mediator 3.0, 1 handler")]
    public ValueTask MediatorOne() => _mediatorTyped.Publish(_mediatorOne);

    [Benchmark(Description = "MediatR 12.5, 5 handlers")]
    public Task MediatRFive() => _mediatrPublisher.Publish(_mediatrFive);

    [Benchmark(Description = "Relevox, 5 handlers")]
    public Task RelevoxFive() => _relevoxTyped.Publish(_relevoxFive);

    [Benchmark(Description = "Mediator 3.0, 5 handlers")]
    public ValueTask MediatorFive() => _mediatorTyped.Publish(_mediatorFive);
}

/// <summary>A stream of 100 items, drained. The difference per item is the stream plumbing.</summary>
[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "Median", "RatioSD")]
public class StreamBenchmarks
{
    private readonly MediatRPeer.Count _mediatrCount = new(100);
    private readonly MediatorPeer.Count _mediatorCount = new(100);
    private readonly RelevoxPeer.Count _relevoxCount = new(100);
    private ServiceProvider _relevox = null!;
    private IServiceScope _relevoxScope = null!;
    private RelevoxMediator _relevoxTyped = null!;
    private ServiceProvider _mediatr = null!;
    private ServiceProvider _mediator = null!;
    private IServiceScope _mediatrScope = null!;
    private IServiceScope _mediatorScope = null!;
    private global::MediatR.ISender _mediatrSender = null!;
    private global::Mediator.Mediator _mediatorTyped = null!;

    [GlobalSetup]
    public void Setup()
    {
        _mediatr = Containers.MediatR(withPipeline: false);
        _mediator = Containers.Mediator(withPipeline: false);
        _relevox = Containers.Relevox(ServiceLifetime.Transient);
        _relevoxScope = _relevox.CreateScope();
        _relevoxTyped = _relevoxScope.ServiceProvider.GetRequiredService<RelevoxMediator>();
        _mediatrScope = _mediatr.CreateScope();
        _mediatorScope = _mediator.CreateScope();
        _mediatrSender = _mediatrScope.ServiceProvider.GetRequiredService<global::MediatR.ISender>();
        _mediatorTyped = _mediatorScope.ServiceProvider.GetRequiredService<global::Mediator.Mediator>();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _mediatrScope.Dispose();
        _mediatorScope.Dispose();
        _mediatr.Dispose();
        _mediator.Dispose();
        _relevoxScope.Dispose();
        _relevox.Dispose();
    }

    [Benchmark(Baseline = true, Description = "MediatR 12.5")]
    public async Task<int> MediatR()
    {
        int sum = 0;
        await foreach (int item in _mediatrSender.CreateStream(_mediatrCount))
        {
            sum += item;
        }

        return sum;
    }

    [Benchmark(Description = "Relevox, typed")]
    public async Task<int> RelevoxTyped()
    {
        int sum = 0;
        await foreach (int item in _relevoxTyped.CreateStream(_relevoxCount))
        {
            sum += item;
        }

        return sum;
    }

    [Benchmark(Description = "Mediator 3.0, typed")]
    public async Task<int> MediatorTyped()
    {
        int sum = 0;
        await foreach (int item in _mediatorTyped.CreateStream(_mediatorCount))
        {
            sum += item;
        }

        return sum;
    }
}

/// <summary>
/// Registering, building the container and sending the first request.
/// </summary>
/// <remarks>
/// After the first iteration the JIT has done its work, so this measures registration and container build,
/// which is where assembly scanning shows up. It is not a cold process start; that number comes from the
/// NativeAOT sample later.
/// </remarks>
[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "Median", "RatioSD")]
public class StartupBenchmarks
{
    [Benchmark(Baseline = true, Description = "MediatR 12.5")]
    public async Task<int> MediatR()
    {
        await using ServiceProvider provider = Containers.MediatR(withPipeline: false);
        return await provider.GetRequiredService<global::MediatR.ISender>().Send(new MediatRPeer.Ping(1));
    }

    [Benchmark(Description = "Relevox")]
    public async Task<int> Relevox()
    {
        await using ServiceProvider provider = Containers.Relevox(ServiceLifetime.Transient);
        return await provider.GetRequiredService<global::Relevox.ISender>().Send(new RelevoxPeer.Ping(1));
    }

    [Benchmark(Description = "Mediator 3.0")]
    public async Task<int> Mediator()
    {
        await using ServiceProvider provider = Containers.Mediator(withPipeline: false);
        return await provider.GetRequiredService<global::Mediator.Mediator>().Send(new MediatorPeer.Ping(1));
    }
}
