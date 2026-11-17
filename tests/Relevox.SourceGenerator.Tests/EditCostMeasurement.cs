using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Relevox.SourceGenerator.Tests;

/// <summary>
/// What one edit costs the generator in a large project, as published in
/// <c>docs/benchmark-results/2026-10-18-ide.md</c> and <c>2026-11-17-edit-cost.md</c>. A measurement, not a test, so
/// it only runs when asked:
/// <c>dotnet run --project tests/Relevox.SourceGenerator.Tests -c Release -- -explicit only -class "*EditCostMeasurement"</c>.
/// </summary>
/// <remarks>
/// The first four rows are the layout of the first measurement: a hundred handlers to a file, nothing sent and no
/// generic handler. The others put each request and its handler in a file of their own, as most projects do,
/// which is what makes a step that walks every file per handler show; send every request from a class beside it,
/// for the message map's call sites; and add generic handlers, which the generator closes on every edit.
/// </remarks>
public sealed class EditCostMeasurement
{
    [Theory(Explicit = true)]
    [InlineData(500, false, false, false, false)]
    [InlineData(2500, false, false, false, false)]
    [InlineData(500, false, false, false, true)]
    [InlineData(2500, false, false, false, true)]
    [InlineData(2500, true, false, false, false)]
    [InlineData(2500, true, true, false, false)]
    [InlineData(2500, true, true, false, true)]
    [InlineData(2500, true, true, true, false)]
    public void An_unrelated_edit(int handlers, bool filePerHandler, bool sends, bool generic, bool messageMap)
    {
        CSharpParseOptions parse = new(LanguageVersion.Preview);
        List<SyntaxTree> trees = [];
        int perFile = filePerHandler ? 1 : 100;
        for (int file = 0; file < handlers / perFile; file++)
        {
            StringBuilder source = new("using Relevox; using System.Threading; using System.Threading.Tasks;\n");
            source.Append(CultureInfo.InvariantCulture, $"namespace N{file};\n");
            for (int i = 0; i < perFile; i++)
            {
                source.Append(CultureInfo.InvariantCulture, $"public sealed record Q{i}(int Id) : IRequest<int>;\npublic sealed class Q{i}Handler : IRequestHandler<Q{i}, int> {{ public Task<int> Handle(Q{i} r, CancellationToken c) => Task.FromResult(r.Id); }}\n");

                // No base list, so source discovery does not look at it; only the call sites do.
                if (sends)
                {
                    source.Append(CultureInfo.InvariantCulture, $"public sealed class Q{i}Caller {{ public Task<int> Go(ISender s, CancellationToken c) => s.Send(new Q{i}(1), c); }}\n");
                }
            }

            trees.Add(CSharpSyntaxTree.ParseText(source.ToString(), parse, $"F{file}.cs"));
        }

        // With the map on, every request is checked against an open behaviour from the options.
        trees.Add(CSharpSyntaxTree.ParseText(
            """
            using Microsoft.Extensions.DependencyInjection; using Relevox; using System.Threading; using System.Threading.Tasks;
            public sealed class Logging<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
            {
                public Task<TResponse> Handle(TRequest r, RequestHandlerDelegate<TResponse> n, CancellationToken c) => n(c);
            }
            public static class Setup
            {
                public static IServiceCollection Add(IServiceCollection s) => s.AddRelevox(o => o.AddOpenBehavior(typeof(Logging<,>)));
            }
            """,
            parse,
            "Setup.cs"));

        // Ten generic request handlers, each closed over ten entities, as in the solution of the first measurement.
        if (generic)
        {
            StringBuilder source = new("using Relevox; using System.Threading; using System.Threading.Tasks;\nnamespace G;\npublic interface IEntity;\n");
            for (int entity = 0; entity < 10; entity++)
            {
                source.Append(CultureInfo.InvariantCulture, $"public sealed class E{entity} : IEntity;\n");
            }

            for (int handler = 0; handler < 10; handler++)
            {
                source.Append(CultureInfo.InvariantCulture, $"public sealed record Get{handler}<T>(int Id) : IRequest<string> where T : IEntity;\npublic sealed class Get{handler}Handler<T> : IRequestHandler<Get{handler}<T>, string> where T : IEntity {{ public Task<string> Handle(Get{handler}<T> r, CancellationToken c) => Task.FromResult(typeof(T).Name); }}\n");
            }

            trees.Add(CSharpSyntaxTree.ParseText(source.ToString(), parse, "Generic.cs"));
        }

        trees.Add(CSharpSyntaxTree.ParseText(Util(0), parse, "Util.cs"));
        CSharpCompilation compilation = CSharpCompilation.Create("Big", trees, GeneratorHarness.References, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new RelevoxGenerator().AsSourceGenerator()],
            parseOptions: parse,
            optionsProvider: messageMap ? GeneratorHarness.MessageMapOn : null,
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
        driver = driver.RunGenerators(compilation, TestContext.Current.CancellationToken);

        // Sixty edits and the median of the last twenty, so the JIT and Roslyn's caches have settled.
        List<(double Total, double Source, double Closings, double Sites, double Map)> samples = [];
        for (int edit = 1; edit <= 60; edit++)
        {
            compilation = compilation.ReplaceSyntaxTree(compilation.SyntaxTrees[^1], CSharpSyntaxTree.ParseText(Util(edit), parse, "Util.cs"));
            long start = Stopwatch.GetTimestamp();
            driver = driver.RunGenerators(compilation, TestContext.Current.CancellationToken);
            double total = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            GeneratorRunResult result = driver.GetRunResult().Results[0];
            samples.Add((total, Step(result, TrackingNames.Source), Step(result, TrackingNames.Closings), Step(result, TrackingNames.CallSites), Step(result, TrackingNames.MessageMap)));
        }

        List<(double Total, double Source, double Closings, double Sites, double Map)> settled = [.. samples.TakeLast(20)];
        string line = string.Create(
            CultureInfo.InvariantCulture,
            $"{handlers} handlers, {(filePerHandler ? "a file each" : "100 to a file")}, sends {(sends ? "yes" : "no")}, generic {(generic ? "yes" : "no")}, map {(messageMap ? "on" : "off")}: {Median(settled.Select(sample => sample.Total)):F0} ms per edit, discovery in source {Median(settled.Select(sample => sample.Source)):F0} ms, closing generics {Median(settled.Select(sample => sample.Closings)):F0} ms, call sites {Median(settled.Select(sample => sample.Sites)):F0} ms, message map {Median(settled.Select(sample => sample.Map)):F0} ms");
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
