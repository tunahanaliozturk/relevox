using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Relevox.Benchmarks.Scale;

namespace Scale;

/// <summary>
/// Sending one request out of five hundred, through each library's typed and interface paths.
/// </summary>
/// <remarks>
/// The question is whether the interface path stays constant-size as a project grows. fastcast's switch over
/// request types did not: it was twice as slow as MediatR at five hundred. Every library here runs with
/// transient handlers, so the numbers compare dispatch, not lifetime. The rotating rows send eight different
/// request types in turn, so a lookup table cannot stay hot on one key. Error is shown so the noise in each
/// row can be judged.
/// </remarks>
[MemoryDiagnoser]
[HideColumns("StdDev", "Median", "RatioSD")]
public class ScaleBenchmarks
{
    private readonly MediatRPeer.Request250 _mediatrRequest = new(1);
    private readonly RelevoxPeer.Request250 _relevoxRequest = new(1);
    private readonly MediatorPeer.Request250 _mediatorRequest = new(1);
    private ServiceProvider _mediatr = null!;
    private ServiceProvider _relevox = null!;
    private ServiceProvider _mediator = null!;
    private IServiceScope[] _scopes = [];
    private readonly object[] _mediatrRotation = [.. Enumerable.Range(0, 8).Select(index => MediatRRequest(index * 60))];
    private readonly object[] _relevoxRotation = [.. Enumerable.Range(0, 8).Select(index => RelevoxRequest(index * 60))];
    private readonly object[] _mediatorRotation = [.. Enumerable.Range(0, 8).Select(index => MediatorRequest(index * 60))];
    private int _turn;
    private global::MediatR.ISender _mediatrSender = null!;
    private RelevoxMediator _relevoxTyped = null!;
    private global::Relevox.ISender _relevoxSender = null!;
    private global::Mediator.Mediator _mediatorTyped = null!;
    private global::Mediator.ISender _mediatorSender = null!;

    [GlobalSetup]
    public void Setup()
    {
        ServiceCollection mediatr = new();
        mediatr.AddMediatR(configuration => configuration.RegisterServicesFromAssemblyContaining<MediatRPeer.Request0>());
        _mediatr = mediatr.BuildServiceProvider();

        ServiceCollection relevox = new();
        relevox.AddRelevox();
        _relevox = relevox.BuildServiceProvider();

        ServiceCollection mediator = new();
        mediator.AddMediator(options => options.ServiceLifetime = ServiceLifetime.Transient);
        _mediator = mediator.BuildServiceProvider();

        _scopes = [_mediatr.CreateScope(), _relevox.CreateScope(), _mediator.CreateScope()];
        _mediatrSender = _scopes[0].ServiceProvider.GetRequiredService<global::MediatR.ISender>();
        _relevoxTyped = _scopes[1].ServiceProvider.GetRequiredService<RelevoxMediator>();
        _relevoxSender = _scopes[1].ServiceProvider.GetRequiredService<global::Relevox.ISender>();
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
        _relevox.Dispose();
        _mediator.Dispose();
    }

    [Benchmark(Baseline = true, Description = "MediatR 12.5")]
    public Task<int> MediatR() => _mediatrSender.Send(_mediatrRequest);

    [Benchmark(Description = "Relevox, typed")]
    public Task<int> RelevoxTyped() => _relevoxTyped.Send(_relevoxRequest);

    [Benchmark(Description = "Relevox, ISender")]
    public Task<int> RelevoxInterface() => _relevoxSender.Send(_relevoxRequest);

    [Benchmark(Description = "Mediator 3.0 transient, typed")]
    public ValueTask<int> MediatorTyped() => _mediatorTyped.Send(_mediatorRequest);

    [Benchmark(Description = "Mediator 3.0 transient, ISender")]
    public ValueTask<int> MediatorInterface() => _mediatorSender.Send<int>(_mediatorRequest);

    [Benchmark(Description = "MediatR 12.5, 8 types in turn")]
    public Task<int> MediatRRotating() => _mediatrSender.Send((global::MediatR.IRequest<int>)_mediatrRotation[_turn++ & 7]);

    [Benchmark(Description = "Relevox, ISender, 8 types in turn")]
    public Task<int> RelevoxRotating() => _relevoxSender.Send((global::Relevox.IRequest<int>)_relevoxRotation[_turn++ & 7]);

    [Benchmark(Description = "Mediator 3.0 transient, ISender, 8 types in turn")]
    public ValueTask<int> MediatorRotating() => _mediatorSender.Send((global::Mediator.IRequest<int>)_mediatorRotation[_turn++ & 7]);

    // Each library's request types are distinct classes with the same numbering. They are created by name here,
    // in setup, so picking eight of them takes no code per request type.
    private static object MediatRRequest(int index) => Activator.CreateInstance(typeof(MediatRPeer.Request0).Assembly.GetType($"Scale.MediatRPeer.Request{index}")!, 1)!;

    private static object RelevoxRequest(int index) => Activator.CreateInstance(typeof(RelevoxPeer.Request0).Assembly.GetType($"Scale.RelevoxPeer.Request{index}")!, 1)!;

    private static object MediatorRequest(int index) => Activator.CreateInstance(typeof(MediatorPeer.Request0).Assembly.GetType($"Scale.MediatorPeer.Request{index}")!, 1)!;
}
