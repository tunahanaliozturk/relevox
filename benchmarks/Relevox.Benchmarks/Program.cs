using BenchmarkDotNet.Running;
using Benchmarks;

// `--sanity` runs every scenario once per library and checks the answer, so a benchmark that silently
// skips a behaviour or a handler fails loudly instead of producing a flattering number.
if (args is ["--sanity"])
{
    await Sanity.RunAsync();
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
