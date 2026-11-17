using BenchmarkDotNet.Running;
using Scale;

// `--sanity` sends the middle request through every path once and checks the answer before any number is trusted.
if (args is ["--sanity"])
{
    ScaleBenchmarks benchmarks = new();
    benchmarks.Setup();
    int[] answers =
    [
        await benchmarks.MediatR(),
        await benchmarks.RelevoxTyped(),
        await benchmarks.RelevoxInterface(),
        await benchmarks.MediatorTyped(),
        await benchmarks.MediatorInterface(),
        await benchmarks.MediatRScope(),
        await benchmarks.RelevoxScope(),
        await benchmarks.MediatorScope(),
    ];
    int[] rotating = [await benchmarks.MediatRRotating(), await benchmarks.RelevoxRotating(), await benchmarks.MediatorRotating()];
    benchmarks.Cleanup();

    // The three rotating rows share one turn counter, so they reach requests 0, Stride and 2 * Stride.
    if (answers.Any(answer => answer != ScaleSize.Probe) || !rotating.SequenceEqual([0, ScaleSize.Stride, 2 * ScaleSize.Stride]))
    {
        Console.Error.WriteLine($"Unexpected answers: {string.Join(", ", answers)}; rotating {string.Join(", ", rotating)}.");
        return 1;
    }

    Console.WriteLine($"Every path reached request {ScaleSize.Probe}'s handler, out of {ScaleSize.Count}.");
    return 0;
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
return 0;
