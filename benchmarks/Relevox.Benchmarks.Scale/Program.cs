using BenchmarkDotNet.Running;
using Scale;

// `--sanity` sends request 250 through every path once and checks the answer before any number is trusted.
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
    ];
    int[] rotating = [await benchmarks.MediatRRotating(), await benchmarks.RelevoxRotating(), await benchmarks.MediatorRotating()];
    benchmarks.Cleanup();

    // The three rotating rows share one turn counter, so they reach requests 0, 60 and 120.
    if (answers.Any(answer => answer != 250) || !rotating.SequenceEqual([0, 60, 120]))
    {
        Console.Error.WriteLine($"Unexpected answers: {string.Join(", ", answers)}; rotating {string.Join(", ", rotating)}.");
        return 1;
    }

    Console.WriteLine("Every path reached request 250's handler.");
    return 0;
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
return 0;
