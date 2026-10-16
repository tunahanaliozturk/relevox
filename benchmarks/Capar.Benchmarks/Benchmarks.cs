using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace Capar.Benchmarks;

// Benchmarks return the library's own Task or ValueTask instead of awaiting it in an async method. An
// async wrapper would add a state machine to every row and measure that instead of the mediator.
//
// Mediator has two send paths, like Capar will: the generated concrete class, whose overloads the compiler
// binds by request type, and ISender, for callers that only hold the interface. Both are measured because
// real code uses both.

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

    [GlobalSetup]
    public void Setup()
    {
        _mediatr = Containers.MediatR(withPipeline: false);
        _mediator = Containers.Mediator(withPipeline: false);

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
    }

    [Benchmark(Baseline = true, Description = "MediatR 12.5")]
    public Task<int> MediatR() => _mediatrSender.Send(_mediatrPing);

    [Benchmark(Description = "MediatR 12.5, object")]
    public Task<object?> MediatRObject() => _mediatrSender.Send((object)_mediatrPing);

    [Benchmark(Description = "Mediator 3.0, typed")]
    public ValueTask<int> MediatorTyped() => _mediatorTyped.Send(_mediatorPing);

    [Benchmark(Description = "Mediator 3.0, ISender")]
    public ValueTask<int> MediatorInterface() => _mediatorSender.Send<int>(_mediatorPing);

    [Benchmark(Description = "Mediator 3.0, object")]
    public ValueTask<object?> MediatorObject() => _mediatorSender.Send((object)_mediatorPing);
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

    [GlobalSetup]
    public void Setup()
    {
        _mediatr = Containers.MediatR(withPipeline: true);
        _mediator = Containers.Mediator(withPipeline: true);
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
    }

    [Benchmark(Baseline = true, Description = "MediatR 12.5")]
    public Task<int> MediatR() => _mediatrSender.Send(_mediatrPiped);

    [Benchmark(Description = "Mediator 3.0, typed")]
    public ValueTask<int> MediatorTyped() => _mediatorTyped.Send(_mediatorPiped);
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

    [GlobalSetup]
    public void Setup()
    {
        _mediatr = Containers.MediatR(withPipeline: false);
        _mediator = Containers.Mediator(withPipeline: false);
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
    }

    [Benchmark(Baseline = true, Description = "MediatR 12.5, 1 handler")]
    public Task MediatROne() => _mediatrPublisher.Publish(_mediatrOne);

    [Benchmark(Description = "Mediator 3.0, 1 handler")]
    public ValueTask MediatorOne() => _mediatorTyped.Publish(_mediatorOne);

    [Benchmark(Description = "MediatR 12.5, 5 handlers")]
    public Task MediatRFive() => _mediatrPublisher.Publish(_mediatrFive);

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

    [Benchmark(Description = "Mediator 3.0")]
    public async Task<int> Mediator()
    {
        await using ServiceProvider provider = Containers.Mediator(withPipeline: false);
        return await provider.GetRequiredService<global::Mediator.Mediator>().Send(new MediatorPeer.Ping(1));
    }
}
