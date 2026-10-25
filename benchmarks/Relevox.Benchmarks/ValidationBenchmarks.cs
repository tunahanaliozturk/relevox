using BenchmarkDotNet.Attributes;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Relevox.Benchmarks;

namespace Benchmarks;

/// <summary>
/// Relevox.FluentValidation (#100): a request without a validator, sent with and without <c>AddValidation</c>, which
/// should cost the same because the build gives it no behaviour; and a request whose validator passes.
/// </summary>
[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "Median", "RatioSD")]
public class ValidationBenchmarks
{
    private readonly RelevoxPeer.Ping _ping = new(1);
    private readonly RelevoxPeer.Checked _checked = new(1);
    private ServiceProvider _without = null!;
    private ServiceProvider _with = null!;
    private IServiceScope[] _scopes = null!;
    private RelevoxMediator _withoutTyped = null!;
    private RelevoxMediator _withTyped = null!;

    [GlobalSetup]
    public void Setup()
    {
        _without = Containers.Relevox(ServiceLifetime.Transient);
        ServiceCollection services = new();
        services.AddValidatorsFromAssemblyContaining<ValidationBenchmarks>(ServiceLifetime.Transient);
        services.AddRelevox(static options => options.AddValidation());
        _with = services.BuildServiceProvider();
        _scopes = [_without.CreateScope(), _with.CreateScope()];
        _withoutTyped = _scopes[0].ServiceProvider.GetRequiredService<RelevoxMediator>();
        _withTyped = _scopes[1].ServiceProvider.GetRequiredService<RelevoxMediator>();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (IServiceScope scope in _scopes)
        {
            scope.Dispose();
        }

        _without.Dispose();
        _with.Dispose();
    }

    [Benchmark(Baseline = true, Description = "Relevox, typed, no validation")]
    public Task<int> Without() => _withoutTyped.Send(_ping);

    [Benchmark(Description = "Relevox, typed, AddValidation, request without a validator")]
    public Task<int> WithoutValidator() => _withTyped.Send(_ping);

    [Benchmark(Description = "Relevox, typed, AddValidation, request whose validator passes")]
    public Task<int> WithValidator() => _withTyped.Send(_checked);
}
