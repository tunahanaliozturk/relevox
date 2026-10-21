# Telemetry: what a send costs off, on with nothing listening, and on with a listener

Measured for 1.2 on the machine named in every other results file, `TelemetryBenchmarks` in
`benchmarks/Relevox.Benchmarks`, at commit `77cf5da` plus the change below:

```
BenchmarkDotNet v0.15.8, Windows 11 (10.0.26100.9448/24H2)
Intel Core Ultra 7 255H 2.00GHz, 16 logical and 16 physical cores
.NET SDK 10.0.402, runtime .NET 10.0.13, X64 RyuJIT x86-64-v3, DefaultJob
```

A typed send to a singleton handler with no behaviours, so the dispatch is all there is to measure. Each case
runs in a process of its own; the listening case adds an `ActivityListener` that samples every span and is handed
each one when it stops, and a `MeterListener` that takes every measurement, and both throw what they get away.
That is the caller's share of exporting, up to the point where an exporter would queue the span or the
measurement; an exporter's own queueing and batching come on top.

| | Run 1 | Run 2 | Allocated |
|---|---:|---:|---:|
| Telemetry off | 1.15 ns | 1.14 ns | 0 B |
| Telemetry on, nothing listening | 2.01 ns | 1.99 ns | 0 B |
| Telemetry on, listening | 258.0 ns | 261.2 ns | 576 B |

## What these show

- **Off** costs what a send cost in 1.1: the send benchmarks put a singleton typed send at 1.10 ns and a
  transient one at 9.4 ns, against 0.98 and 8.5 ns in the 1.1.0 run, inside that machine's noise.
- **On with nothing listening** adds about 0.9 ns: a check of the activity source and the duration histogram,
  and nothing else.
- **On with a listener** is dominated by `Activity` itself: creating, tagging and stopping a span is most of the
  260 ns and all of the 576 B. That is the price of tracing in .NET, not of Relevox, and it is paid only when
  something is actually recording.

## A regression this run caught

The first run of the middle row read 10.5 to 12.0 ns, against 2.2 to 2.4 ns for the spans alone (commit
`92b0f9a`). Adding metrics had grown the telemetry scope from two fields to five, and the generated code built
and copied it, and entered a try block, even when the scope then turned out empty. The generated code now asks
`TelemetryScope.Listening` first and, with nothing listening, makes the untraced call as it is.
