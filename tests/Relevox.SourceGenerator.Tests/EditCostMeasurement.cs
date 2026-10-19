using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Relevox.SourceGenerator.Tests;

/// <summary>
/// What one edit costs the generator in a large project, as published in
/// <c>docs/benchmark-results/2026-10-18-ide.md</c>. A measurement, not a test, so it only runs when asked:
/// <c>dotnet run --project tests/Relevox.SourceGenerator.Tests -c Release -- --explicit only</c>.
/// </summary>
public sealed class EditCostMeasurement
{
    [Theory(Explicit = true)]
    [InlineData(500)]
    [InlineData(2500)]
    public void An_unrelated_edit(int handlers)
    {
        CSharpParseOptions parse = new(LanguageVersion.Preview);
        List<SyntaxTree> trees = [];
        for (int file = 0; file < handlers / 100; file++)
        {
            StringBuilder source = new("using Relevox; using System.Threading; using System.Threading.Tasks;\n");
            source.Append(CultureInfo.InvariantCulture, $"namespace N{file};\n");
            for (int i = 0; i < 100; i++)
            {
                source.Append(CultureInfo.InvariantCulture, $"public sealed record Q{i}(int Id) : IRequest<int>;\npublic sealed class Q{i}Handler : IRequestHandler<Q{i}, int> {{ public Task<int> Handle(Q{i} r, CancellationToken c) => Task.FromResult(r.Id); }}\n");
            }

            trees.Add(CSharpSyntaxTree.ParseText(source.ToString(), parse, $"F{file}.cs"));
        }

        trees.Add(CSharpSyntaxTree.ParseText(Util(0), parse, "Util.cs"));
        CSharpCompilation compilation = CSharpCompilation.Create("Big", trees, GeneratorHarness.References, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new RelevoxGenerator().AsSourceGenerator()],
            parseOptions: parse,
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
        driver = driver.RunGenerators(compilation, TestContext.Current.CancellationToken);

        // Sixty edits and the median of the last twenty, so the JIT and Roslyn's caches have settled.
        List<(double Total, double Source, double Closings)> samples = [];
        for (int edit = 1; edit <= 60; edit++)
        {
            compilation = compilation.ReplaceSyntaxTree(compilation.SyntaxTrees[^1], CSharpSyntaxTree.ParseText(Util(edit), parse, "Util.cs"));
            long start = Stopwatch.GetTimestamp();
            driver = driver.RunGenerators(compilation, TestContext.Current.CancellationToken);
            double total = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            GeneratorRunResult result = driver.GetRunResult().Results[0];
            samples.Add((total, Step(result, TrackingNames.Source), Step(result, TrackingNames.Closings)));
        }

        List<(double Total, double Source, double Closings)> settled = [.. samples.TakeLast(20)];
        string line = string.Create(
            CultureInfo.InvariantCulture,
            $"{handlers} handlers: {Median(settled.Select(sample => sample.Total)):F0} ms per edit, discovery in source {Median(settled.Select(sample => sample.Source)):F0} ms, closing generics {Median(settled.Select(sample => sample.Closings)):F0} ms");
        TestContext.Current.TestOutputHelper?.WriteLine(line);
        Console.WriteLine(line);
    }

    private static string Util(int edit) => $"public static class Util {{ public static int One() => {edit}; }}";

    private static double Step(GeneratorRunResult result, string name) =>
        result.TrackedSteps.TryGetValue(name, out ImmutableArray<IncrementalGeneratorRunStep> steps)
            ? steps.Sum(step => step.ElapsedTime.TotalMilliseconds)
            : 0;

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = [.. values.Order()];
        return sorted[sorted.Length / 2];
    }
}
