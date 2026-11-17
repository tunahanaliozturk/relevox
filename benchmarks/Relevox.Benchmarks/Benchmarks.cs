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

    // The object overload with singleton handlers, next to the transient row above. Mediator has no row of its
    // own for it: its handlers are singletons already, so its object row below is the like-for-like one. MediatR
    // has none either: its handlers are transient whatever its Lifetime setting says, so it has nothing to put here.
    [Benchmark(Description = "Relevox singletons, object")]
    public Task<object?> RelevoxSingletonObject() => _relevoxSingletonSender.Send((object)_relevoxPing);

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
            ActivityStopped = static _ => { },
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
    private ServiceProvider _mediatrSingleton = null!;
    private ServiceProvider _relevoxSingleton = null!;
    private ServiceProvider _relevoxSingletonBehaviors = null!;
    private IServiceScope _relevoxScope = null!;
    private RelevoxMediator _relevoxTyped = null!;
    private global::MediatR.ISender _mediatrSingletonSender = null!;
    private RelevoxMediator _relevoxSingletonTyped = null!;
    private RelevoxMediator _relevoxSingletonBehaviorsTyped = null!;

    [GlobalSetup]
    public void Setup()
    {
        _mediatr = Containers.MediatR(withPipeline: true);
        _mediator = Containers.Mediator(withPipeline: true);
        _relevox = Containers.Relevox(ServiceLifetime.Transient, withPipeline: true);
        _mediatrSingleton = Containers.MediatR(withPipeline: true, behaviorLifetime: ServiceLifetime.Singleton);
        _relevoxSingleton = Containers.Relevox(ServiceLifetime.Singleton, withPipeline: true);
        _relevoxSingletonBehaviors = Containers.Relevox(ServiceLifetime.Transient, withPipeline: true, behaviorLifetime: ServiceLifetime.Singleton);
        _mediatrSingletonSender = _mediatrSingleton.GetRequiredService<global::MediatR.ISender>();
        _relevoxSingletonTyped = _relevoxSingleton.GetRequiredService<RelevoxMediator>();
        _relevoxSingletonBehaviorsTyped = _relevoxSingletonBehaviors.GetRequiredService<RelevoxMediator>();
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
        _mediatrSingleton.Dispose();
        _relevoxSingleton.Dispose();
        _relevoxSingletonBehaviors.Dispose();
    }

    [Benchmark(Baseline = true, Description = "MediatR 12.5")]
    public Task<int> MediatR() => _mediatrSender.Send(_mediatrPiped);

    [Benchmark(Description = "Relevox, typed")]
    public Task<int> RelevoxTyped() => _relevoxTyped.Send(_relevoxPiped);

    [Benchmark(Description = "Mediator 3.0, typed")]
    public ValueTask<int> MediatorTyped() => _mediatorTyped.Send(_mediatorPiped);

    // The same three behaviours registered as singletons. Mediator's container registers its behaviours as
    // singletons already, so its row above is the like-for-like one and has no twin. MediatR's handlers cannot be
    // made singletons, so its row changes the behaviours only; Relevox has that row too, to compare with it, and
    // one with singleton handlers as well, to compare with Mediator.
    [Benchmark(Description = "MediatR 12.5, singleton behaviours")]
    public Task<int> MediatRSingletonBehaviors() => _mediatrSingletonSender.Send(_mediatrPiped);

    [Benchmark(Description = "Relevox, typed, singleton behaviours")]
    public Task<int> RelevoxSingletonBehaviorsTyped() => _relevoxSingletonBehaviorsTyped.Send(_relevoxPiped);

    [Benchmark(Description = "Relevox singletons, typed")]
    public Task<int> RelevoxSingletonTyped() => _relevoxSingletonTyped.Send(_relevoxPiped);
}

/// <summary>
/// The three-behaviour send with <c>PipelineOrder</c> on each behaviour (#97): registered in that order, where
/// nothing moves, and in reverse, where the behaviours are put in order on every send.
/// </summary>
[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "Median", "RatioSD")]
public class PipelineOrderBenchmarks
{
    private readonly RelevoxPeer.Piped _piped = new(1);
    private ServiceProvider _plain = null!;
    private ServiceProvider _inOrder = null!;
    private ServiceProvider _reversed = null!;
    private IServiceScope[] _scopes = null!;
    private RelevoxMediator _plainTyped = null!;
    private RelevoxMediator _inOrderTyped = null!;
    private RelevoxMediator _reversedTyped = null!;

    [GlobalSetup]
    public void Setup()
    {
        _plain = Containers.Relevox(ServiceLifetime.Transient, withPipeline: true);
        _inOrder = Containers.RelevoxOrdered(reversed: false);
        _reversed = Containers.RelevoxOrdered(reversed: true);
        _scopes = [_plain.CreateScope(), _inOrder.CreateScope(), _reversed.CreateScope()];
        _plainTyped = _scopes[0].ServiceProvider.GetRequiredService<RelevoxMediator>();
        _inOrderTyped = _scopes[1].ServiceProvider.GetRequiredService<RelevoxMediator>();
        _reversedTyped = _scopes[2].ServiceProvider.GetRequiredService<RelevoxMediator>();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (IServiceScope scope in _scopes)
        {
            scope.Dispose();
        }

        _plain.Dispose();
        _inOrder.Dispose();
        _reversed.Dispose();
    }

    [Benchmark(Baseline = true, Description = "Relevox, typed, no attribute")]
    public Task<int> Plain() => _plainTyped.Send(_piped);

    [Benchmark(Description = "Relevox, typed, PipelineOrder, registered in order")]
    public Task<int> InOrder() => _inOrderTyped.Send(_piped);

    [Benchmark(Description = "Relevox, typed, PipelineOrder, registered in reverse")]
    public Task<int> Reversed() => _reversedTyped.Send(_piped);
}

/// <summary>
/// The pipeline send again, in an application that also configures a pre- and a post-processor for a different
/// request.
/// </summary>
/// <remarks>
/// Both libraries then add their built-in processor behaviours for every request, so Piped carries two
/// behaviours with nothing to do on top of its three. This is the shape of most real applications: a
/// validation or logging processor for some requests, behaviours for others. The last two rows send Ping
/// instead, whose pre- and post-processor run.
/// </remarks>
[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "Median", "RatioSD")]
public class BuiltInBenchmarks
{
    private readonly MediatRPeer.Piped _mediatrPiped = new(1);
    private readonly RelevoxPeer.Piped _relevoxPiped = new(1);
    private readonly MediatRPeer.Ping _mediatrPing = new(1);
    private readonly RelevoxPeer.Ping _relevoxPing = new(1);
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

    [Benchmark(Description = "MediatR 12.5, processors that run")]
    public Task<int> MediatRProcessors() => _mediatrSender.Send(_mediatrPing);

    [Benchmark(Description = "Relevox, typed, processors that run")]
    public Task<int> RelevoxProcessors() => _relevoxTyped.Send(_relevoxPing);
}

/// <summary>
/// Notifications to one and to five handlers, with each library's default publisher; then Relevox with singleton
/// handlers, and with singleton handlers and <c>TaskWhenAllPublisher</c>.
/// </summary>
/// <remarks>
/// Mediator's rows are all singleton handlers with its foreach-await publisher, so they are the like-for-like
/// ones for the singleton rows and have no twins. It has no row for the task-when-all publisher: the publisher
/// is read from the code at compile time and there is one Mediator per assembly, so this assembly cannot have a
/// second one with a different publisher. MediatR has no singleton rows: its handlers are transient whatever
/// its Lifetime setting says.
/// </remarks>
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
    private ServiceProvider _relevoxSingleton = null!;
    private ServiceProvider _relevoxWhenAll = null!;
    private IServiceScope _relevoxScope = null!;
    private RelevoxMediator _relevoxTyped = null!;
    private RelevoxMediator _relevoxSingletonTyped = null!;
    private RelevoxMediator _relevoxWhenAllTyped = null!;

    [GlobalSetup]
    public void Setup()
    {
        _mediatr = Containers.MediatR(withPipeline: false);
        _mediator = Containers.Mediator(withPipeline: false);
        _relevox = Containers.Relevox(ServiceLifetime.Transient);
        _relevoxSingleton = Containers.Relevox(ServiceLifetime.Singleton);
        _relevoxWhenAll = Containers.Relevox(ServiceLifetime.Singleton, whenAll: true);
        _relevoxSingletonTyped = _relevoxSingleton.GetRequiredService<RelevoxMediator>();
        _relevoxWhenAllTyped = _relevoxWhenAll.GetRequiredService<RelevoxMediator>();
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
        _relevoxSingleton.Dispose();
        _relevoxWhenAll.Dispose();
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

    [Benchmark(Description = "Relevox singletons, 1 handler")]
    public Task RelevoxSingletonOne() => _relevoxSingletonTyped.Publish(_relevoxOne);

    [Benchmark(Description = "Relevox singletons, 5 handlers")]
    public Task RelevoxSingletonFive() => _relevoxSingletonTyped.Publish(_relevoxFive);

    [Benchmark(Description = "Relevox singletons, TaskWhenAll, 1 handler")]
    public Task RelevoxWhenAllOne() => _relevoxWhenAllTyped.Publish(_relevoxOne);

    [Benchmark(Description = "Relevox singletons, TaskWhenAll, 5 handlers")]
    public Task RelevoxWhenAllFive() => _relevoxWhenAllTyped.Publish(_relevoxFive);
}

/// <summary>
/// A scope per call: create it from a container built once, resolve <c>ISender</c> from it, send, dispose it. This
/// is what a web request does, and the one place where the lifetime of the mediator itself shows up in a send.
/// </summary>
/// <remarks>
/// MediatR registers its mediator as transient and Relevox does by default, so each of them builds a mediator per
/// scope. Mediator registers it as a singleton and this project builds it that way, so for Mediator the scope
/// hands back the one it already has. That is the comparison asked for, not an oversight: it is the default a
/// project gets. Relevox's handlers are singletons here, which is Mediator's setting and so like for like;
/// MediatR's stay at its default, transient. Every row awaits inside an async method because the scope must
/// outlive the send, and returns a ValueTask so that a send that completes at once allocates nothing for the
/// wrapper in any row.
/// </remarks>
[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "Median", "RatioSD")]
public class ScopeBenchmarks
{
    private readonly MediatRPeer.Ping _mediatrPing = new(1);
    private readonly RelevoxPeer.Ping _relevoxPing = new(1);
    private readonly MediatorPeer.Ping _mediatorPing = new(1);
    private ServiceProvider _mediatr = null!;
    private ServiceProvider _relevox = null!;
    private ServiceProvider _mediator = null!;

    [GlobalSetup]
    public void Setup()
    {
        _mediatr = Containers.MediatR(withPipeline: false);
        _relevox = Containers.Relevox(ServiceLifetime.Singleton);
        _mediator = Containers.Mediator(withPipeline: false);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _mediatr.Dispose();
        _relevox.Dispose();
        _mediator.Dispose();
    }

    [Benchmark(Baseline = true, Description = "MediatR 12.5")]
    public async ValueTask<int> MediatR()
    {
        using IServiceScope scope = _mediatr.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<global::MediatR.ISender>().Send(_mediatrPing);
    }

    [Benchmark(Description = "Relevox singletons")]
    public async ValueTask<int> Relevox()
    {
        using IServiceScope scope = _relevox.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<global::Relevox.ISender>().Send(_relevoxPing);
    }

    [Benchmark(Description = "Mediator 3.0")]
    public async ValueTask<int> Mediator()
    {
        using IServiceScope scope = _mediator.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<global::Mediator.ISender>().Send<int>(_mediatorPing);
    }
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
