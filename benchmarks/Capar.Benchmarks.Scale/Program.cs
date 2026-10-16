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
        await benchmarks.CaparTyped(),
        await benchmarks.CaparInterface(),
        await benchmarks.MediatorTyped(),
        await benchmarks.MediatorInterface(),
    ];
    benchmarks.Cleanup();

    if (answers.Any(answer => answer != 250))
    {
        Console.Error.WriteLine($"Expected 250 from every path, got {string.Join(", ", answers)}.");
        return 1;
    }

    Console.WriteLine("Every path reached request 250's handler.");
    return 0;
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
return 0;
