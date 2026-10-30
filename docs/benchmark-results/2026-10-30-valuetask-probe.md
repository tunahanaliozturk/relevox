# ValueTask handlers: a probe of the send's shape

The measurement behind [ADR 0023](../adr/0023-no-valuetask-handlers.md) (#143). It does not run Relevox: it
reproduces by hand the shape of a generated send with no behaviours, so the only thing that changes between rows
is the type the handler returns and the type the send returns.

```
BenchmarkDotNet v0.15.8, Windows 11 (10.0.26100.9711/24H2)
Intel Core Ultra 7 255H 2.00GHz, 16 logical and 16 physical cores
.NET SDK 10.0.402, runtime .NET 10.0.13, X64 RyuJIT x86-64-v3, DefaultJob
```

The machine was not idle, which shows in the standard deviations of the synchronous rows. The conclusion does not
rest on those nanoseconds: whether the 72 B allocation goes away is decided by the shape, not by noise.

| Method                 | Mean         | Error      | StdDev     | Median       | Ratio  | RatioSD | Gen0   | Allocated | Alloc Ratio |
|----------------------- |-------------:|-----------:|-----------:|-------------:|-------:|--------:|-------:|----------:|------------:|
| TaskHandler_Sync       |     7.869 ns |  0.5209 ns |  1.5359 ns |     7.663 ns |   1.04 |    0.29 | 0.0057 |      72 B |        1.00 |
| ValueTaskHandler_Sync  |    18.133 ns |  0.3854 ns |  0.9740 ns |    18.299 ns |   2.39 |    0.48 | 0.0057 |      72 B |        1.00 |
| ValueTaskSend_Sync     |     3.239 ns |  0.1968 ns |  0.5803 ns |     3.437 ns |   0.43 |    0.11 |      - |         - |        0.00 |
| TaskHandler_Async      | 1,167.110 ns | 13.8928 ns | 12.9953 ns | 1,169.253 ns | 153.99 |   29.74 | 0.0153 |     206 B |        2.86 |
| ValueTaskHandler_Async | 1,166.102 ns | 13.6852 ns | 12.8011 ns | 1,166.294 ns | 153.86 |   29.71 | 0.0172 |     220 B |        3.06 |
| ValueTaskSend_Async    | 1,155.090 ns |  7.4675 ns |  6.9851 ns | 1,153.953 ns | 152.41 |   29.40 | 0.0172 |     220 B |        3.06 |

- `TaskHandler_*`: `Task<int>` send, `Task<int>` handler, as Relevox and MediatR are today.
- `ValueTaskHandler_*`: `Task<int>` send, `ValueTask<int>` handler, which is what 1.7 planned. A synchronous
  result becomes `Task.FromResult`, an asynchronous one `AsTask()`.
- `ValueTaskSend_*`: `ValueTask<int>` send, `ValueTask<int>` handler. `ISender` has no such method.
- `_Sync` handlers return a completed result; `_Async` ones await `Task.Yield()` first, which is most of the
  1.1 microseconds. Results are offset by 1,000 so `Task.FromResult` cannot hand back one of its cached tasks.

## The probe

Built as a plain console project referencing BenchmarkDotNet 0.15.8 and run with `dotnet run -c Release`.

```csharp
// #143 probe: the shape of a generated send, Task handler against ValueTask handler, with ISender.Send returning
// Task<TResponse> as today, and a hypothetical ValueTask-returning send as the ceiling.
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

BenchmarkRunner.Run<SendShape>();

public sealed record Ping(int Id);

public sealed class TaskHandler
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public Task<int> Sync(Ping request, CancellationToken cancellationToken) => Task.FromResult(request.Id + 1000);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public async Task<int> Async(Ping request, CancellationToken cancellationToken)
    {
        await Task.Yield();
        return request.Id + 1000;
    }
}

public sealed class ValueTaskHandler
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public ValueTask<int> Sync(Ping request, CancellationToken cancellationToken) => new(request.Id + 1000);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public async ValueTask<int> Async(Ping request, CancellationToken cancellationToken)
    {
        await Task.Yield();
        return request.Id + 1000;
    }
}

[MemoryDiagnoser]
public class SendShape
{
    private readonly TaskHandler _task = new();
    private readonly ValueTaskHandler _valueTask = new();
    private readonly Ping _ping = new(1);

    // Today: Send returns the handler's Task.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private Task<int> SendTask(Ping request, bool async, CancellationToken cancellationToken) =>
        async ? _task.Async(request, cancellationToken) : _task.Sync(request, cancellationToken);

    // A ValueTask handler behind the same Task-returning Send.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private Task<int> SendValueTask(Ping request, bool async, CancellationToken cancellationToken)
    {
        ValueTask<int> pending = async ? _valueTask.Async(request, cancellationToken) : _valueTask.Sync(request, cancellationToken);
        return pending.IsCompletedSuccessfully ? Task.FromResult(pending.Result) : pending.AsTask();
    }

    // The ceiling: a send that returns the ValueTask itself, which ISender does not.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private ValueTask<int> SendValueTaskThrough(Ping request, bool async, CancellationToken cancellationToken) =>
        async ? _valueTask.Async(request, cancellationToken) : _valueTask.Sync(request, cancellationToken);

    [Benchmark(Baseline = true)] public Task<int> TaskHandler_Sync() => SendTask(_ping, false, default);
    [Benchmark] public Task<int> ValueTaskHandler_Sync() => SendValueTask(_ping, false, default);
    [Benchmark] public ValueTask<int> ValueTaskSend_Sync() => SendValueTaskThrough(_ping, false, default);
    [Benchmark] public async Task<int> TaskHandler_Async() => await SendTask(_ping, true, default);
    [Benchmark] public async Task<int> ValueTaskHandler_Async() => await SendValueTask(_ping, true, default);
    [Benchmark] public async Task<int> ValueTaskSend_Async() => await SendValueTaskThrough(_ping, true, default);
}
```
