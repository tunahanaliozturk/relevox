using BenchmarkDotNet.Attributes;
using Capar.Benchmarks.Scale;
using Microsoft.Extensions.DependencyInjection;

namespace Scale;

/// <summary>
/// Sending one request out of five hundred, through each library's typed and interface paths.
/// </summary>
/// <remarks>
/// The question is whether the interface path stays constant-size as a project grows. fastcast's switch over
/// request types did not: it was twice as slow as MediatR at five hundred. Every library here runs with
/// transient handlers, so the numbers compare dispatch, not lifetime.
/// </remarks>
[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "Median", "RatioSD")]
public class ScaleBenchmarks
{
    private readonly MediatRPeer.Request250 _mediatrRequest = new(1);
    private readonly CaparPeer.Request250 _caparRequest = new(1);
    private readonly MediatorPeer.Request250 _mediatorRequest = new(1);
    private ServiceProvider _mediatr = null!;
    private ServiceProvider _capar = null!;
    private ServiceProvider _mediator = null!;
    private IServiceScope[] _scopes = [];
    private global::MediatR.ISender _mediatrSender = null!;
    private CaparMediator _caparTyped = null!;
    private global::Capar.ISender _caparSender = null!;
    private global::Mediator.Mediator _mediatorTyped = null!;
    private global::Mediator.ISender _mediatorSender = null!;

    [GlobalSetup]
    public void Setup()
    {
        ServiceCollection mediatr = new();
        mediatr.AddMediatR(configuration => configuration.RegisterServicesFromAssemblyContaining<MediatRPeer.Request0>());
        _mediatr = mediatr.BuildServiceProvider();

        ServiceCollection capar = new();
        capar.AddCapar();
        _capar = capar.BuildServiceProvider();

        ServiceCollection mediator = new();
        mediator.AddMediator(options => options.ServiceLifetime = ServiceLifetime.Transient);
        _mediator = mediator.BuildServiceProvider();

        _scopes = [_mediatr.CreateScope(), _capar.CreateScope(), _mediator.CreateScope()];
        _mediatrSender = _scopes[0].ServiceProvider.GetRequiredService<global::MediatR.ISender>();
        _caparTyped = _scopes[1].ServiceProvider.GetRequiredService<CaparMediator>();
        _caparSender = _scopes[1].ServiceProvider.GetRequiredService<global::Capar.ISender>();
        _mediatorTyped = _scopes[2].ServiceProvider.GetRequiredService<global::Mediator.Mediator>();
        _mediatorSender = _scopes[2].ServiceProvider.GetRequiredService<global::Mediator.ISender>();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (IServiceScope scope in _scopes)
        {
            scope.Dispose();
        }

        _mediatr.Dispose();
        _capar.Dispose();
        _mediator.Dispose();
    }

    [Benchmark(Baseline = true, Description = "MediatR 12.5")]
    public Task<int> MediatR() => _mediatrSender.Send(_mediatrRequest);

    [Benchmark(Description = "Capar, typed")]
    public Task<int> CaparTyped() => _caparTyped.Send(_caparRequest);

    [Benchmark(Description = "Capar, ISender")]
    public Task<int> CaparInterface() => _caparSender.Send(_caparRequest);

    [Benchmark(Description = "Mediator 3.0 transient, typed")]
    public ValueTask<int> MediatorTyped() => _mediatorTyped.Send(_mediatorRequest);

    [Benchmark(Description = "Mediator 3.0 transient, ISender")]
    public ValueTask<int> MediatorInterface() => _mediatorSender.Send<int>(_mediatorRequest);
}
