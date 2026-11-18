# Held handlers: per container instead of per mediator

For #165 and [ADR 0026](../adr/0026-singletons-held-per-container.md). Before is `05e867b` (main), after is the
change on `perf/singletons-per-container`. Every row is one that already existed (#164); nothing was added to the
benchmarks.

Intel Core Ultra 7 255H, 16 cores, Windows 11 24H2, .NET SDK 11.0.100, benchmarks on .NET 10.0.15, BenchmarkDotNet
0.15.8.

## How it was measured

The machine was not quiet: another project was building on it, and memory was short. Timings on it move by a
quarter between sessions for the same binary, so the change is judged on allocated bytes, which BenchmarkDotNet's
memory diagnoser counts exactly and which load does not move. Timings are given too, as indicative only.

Each side was built into its own folder and run in process with a short job, which is enough for allocations:

```bash
dotnet <out>/Relevox.Benchmarks.dll --filter '*ScopeBenchmarks*' '*SendBenchmarks*' '*PipelineBenchmarks*' '*PublishBenchmarks*' --job short --inProcess
ScaleCount=2500 dotnet build -c Release benchmarks/Relevox.Benchmarks.Scale
dotnet benchmarks/Relevox.Benchmarks.Scale/bin/n2500/Release/net10.0/Relevox.Benchmarks.Scale.dll --filter '*' --job short --inProcess
```

For the timings, before and after were run in turn in one session, three rounds of each: the main benchmarks
before, after, before, after, before, after, then the scale project's scope rows the same way at each size. A
timing is only called better or worse below when the three rounds of one side do not overlap the three of the
other.

## Scope per send

Create a scope, resolve `ISender`, send once, dispose. The main benchmarks run it with singleton handlers, the scale
project with the default transient ones.

| Row | Before | After |
|---|---:|---:|
| Main benchmarks, `Relevox singletons` | 264 B | 192 B |
| Scale 100, `Relevox, ISender, scope per send` | 1,096 B | 216 B |
| Scale 500, `Relevox, ISender, scope per send` | 4,696 B | 216 B |
| Scale 2,500, `Relevox, ISender, scope per send` | 22,696 B | 216 B |

Every run of each row gave the same number. The mediator built per scope no longer grows with the number of
handlers; what is left is the scope, the container's work and a mediator of fixed size. For comparison, in the
same runs MediatR's row allocated 288 B in the scale project and Mediator's 256 B (transient there) and 128 B
(singleton, in the main benchmarks).

The lookups: building a mediator asks the container for one thing, the container's `HeldHandlers`, where it asked
for `PipelineState`. A singleton handler the container already holds is not asked for again by a new mediator,
which `HeldHandlerTests` counts. So a scope's send with a singleton handler asks once instead of twice.

Timings, mean per round in nanoseconds, before and after:

| Row | Before | After | Overlap |
|---|---|---|---|
| Main, `Relevox singletons` | 96.8, 84.5, 112.9 | 78.7, 108.9, 81.6 | yes |
| Scale 100 | 195.2, 190.1, 234.5 | 107.9, 111.7, 101.0 | no |
| Scale 500 | 528.3, 610.6, 640.5 | 134.6, 124.2, 110.6 | no |
| Scale 2,500 | 2,536.2, 1,818.2, 1,780.6 | 104.8, 117.0, 125.0 | no |

In the scale project the before and after rounds are far apart at every size, and the before rows grow with the
size where the after rows do not, so that is a timing difference and not noise. With few handlers, in the main
benchmarks, the rounds overlap and no timing claim is made. MediatR's scope row in the same rounds: 114 to 132 ns
at 100, 123 to 146 at 500, 101 to 145 at 2,500.

## Rows that hold one mediator

These must not allocate more. They allocate exactly what they did, in every round of every run.

| Row | Before | After |
|---|---:|---:|
| Send, `Relevox, typed` | 24 B | 24 B |
| Send, `Relevox, ISender` | 24 B | 24 B |
| Send, `Relevox, object` | 120 B | 120 B |
| Send, `Relevox singletons, typed` | 0 B | 0 B |
| Send, `Relevox singletons, ISender` | 0 B | 0 B |
| Send, `Relevox singletons, object` | 96 B | 96 B |
| Pipeline, `Relevox, typed` | 392 B | 392 B |
| Pipeline, `Relevox, typed, singleton behaviours` | 272 B | 272 B |
| Pipeline, `Relevox singletons, typed` | 248 B | 248 B |
| Publish, `Relevox, 1 handler` | 24 B | 24 B |
| Publish, `Relevox, 5 handlers` | 120 B | 120 B |
| Publish, `Relevox singletons, 1 handler` | 0 B | 0 B |
| Publish, `Relevox singletons, 5 handlers` | 0 B | 0 B |
| Publish, `Relevox singletons, TaskWhenAll, 1 handler` | 328 B | 328 B |
| Publish, `Relevox singletons, TaskWhenAll, 5 handlers` | 880 B | 880 B |
| Scale 100, 500 and 2,500: typed, `ISender`, 8 types in turn | 24 B each | 24 B each |

Timings, mean per round in nanoseconds:

| Row | Before | After | Overlap |
|---|---|---|---|
| Send, `Relevox, typed` | 13.8, 14.5, 11.3 | 11.2, 11.1, 13.9 | yes |
| Send, `Relevox, ISender` | 19.2, 18.9, 18.6 | 16.9, 19.9, 22.5 | yes |
| Send, `Relevox, object` | 34.5, 26.4, 29.9 | 27.7, 30.0, 37.0 | yes |
| Send, `Relevox singletons, typed` | 2.6, 2.4, 2.0 | 1.1, 0.7, 0.8 | no, after lower |
| Send, `Relevox singletons, ISender` | 7.4, 8.9, 10.7 | 7.5, 9.2, 9.2 | yes |
| Send, `Relevox singletons, object` | 22.4, 21.6, 16.4 | 23.4, 21.1, 22.2 | yes |
| Pipeline, `Relevox, typed` | 79.3, 76.5, 66.2 | 87.5, 70.6, 86.5 | yes |
| Pipeline, `Relevox, typed, singleton behaviours` | 72.7, 53.0, 53.9 | 60.5, 58.8, 75.0 | yes |
| Pipeline, `Relevox singletons, typed` | 48.4, 51.8, 48.6 | 52.0, 48.2, 64.9 | yes |
| Publish, `Relevox, 1 handler` | 18.1, 17.5, 22.4 | 15.2, 14.1, 17.7 | yes |
| Publish, `Relevox, 5 handlers` | 60.2, 62.9, 58.1 | 58.8, 58.0, 65.5 | yes |
| Publish, `Relevox singletons, 1 handler` | 7.2, 8.9, 8.7 | 7.8, 7.1, 8.3 | yes |
| Publish, `Relevox singletons, 5 handlers` | 17.8, 16.6, 16.6 | 16.7, 18.0, 16.5 | yes |
| Publish, `Relevox singletons, TaskWhenAll, 1 handler` | 42.3, 44.6, 47.1 | 45.3, 42.0, 47.5 | yes |
| Publish, `Relevox singletons, TaskWhenAll, 5 handlers` | 172.2, 167.3, 200.5 | 187.5, 162.9, 186.6 | yes |

Nothing is clearly worse. The typed singleton send came out below the before rounds in all three, at about 1 ns; at
that size a difference in the code the JIT lays out is enough to explain it, so it is reported and not claimed.
The pipeline rows read a little higher after in two rounds, and MediatR's pipeline row, which this change cannot
touch, read higher in the same rounds (135.6 to 145.0 before, 147.6 to 188.5 after), which is the machine.

### A first version that was slower

The first version of the change sent transient handlers through the same out-of-line generic method as singletons
and scoped ones. In a session like the one above, publishing to five transient handlers measured 65.0, 56.5 and
70.5 ns before and 83.2, 70.3 and 91.0 after: barely overlapping, and the after rounds a fifth higher. A shared
generic method resolves its type through a runtime lookup, five times per publish here. The accessor now resolves a
transient handler itself, with its type known, as it did before, and the rounds above are from that version.

## Singleton behaviours

Measured with a benchmark class that was not committed, on the containers the pipeline rows use: from a scope,
`GetServices<IPipelineBehavior<Piped, int>>()`, which is what `PipelineState` asks the container on each send with
behaviours.

| Behaviours | Mean | Allocated |
|---|---:|---:|
| Three singletons | 7.7 ns | 0 B |
| Three transient | 21.0 ns | 120 B |

With all three singletons the container returned the same array on two calls, checked by reference. Holding them
in Relevox would save the 7.7 ns lookup and no bytes, so it was not done, and ADR 0004 was corrected instead (ADR
0026 gives the other reasons).
