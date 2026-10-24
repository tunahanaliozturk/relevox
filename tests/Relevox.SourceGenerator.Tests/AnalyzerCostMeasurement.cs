using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Diagnostics.Telemetry;
using Relevox.Migration;

namespace Relevox.SourceGenerator.Tests;

/// <summary>
/// What each Relevox analyzer costs a full analysis of a large project, as Roslyn times it. A measurement, not a
/// test, so it only runs when asked:
/// <c>dotnet run --project tests/Relevox.SourceGenerator.Tests -c Release -- -explicit only -class "*AnalyzerCostMeasurement"</c>.
/// </summary>
public sealed class AnalyzerCostMeasurement
{
    [Fact(Explicit = true)]
    public async Task Analysis_of_2500_handlers()
    {
        CSharpParseOptions parse = new(LanguageVersion.Preview);
        List<SyntaxTree> trees = [];
        for (int file = 0; file < 25; file++)
        {
            StringBuilder source = new("using Relevox; using System.Threading; using System.Threading.Tasks;\n");
            source.Append(CultureInfo.InvariantCulture, $"namespace N{file};\n");
            for (int i = 0; i < 100; i++)
            {
                // Every tenth handler sends the next request, so the send rules have calls to look at.
                string body = i % 10 == 0 && i < 99
                    ? $"sender.Send(new Q{i + 1}(r.Id), c)"
                    : "Task.FromResult(r.Id)";
                source.Append(CultureInfo.InvariantCulture, $"public sealed record Q{i}(int Id) : IRequest<int>;\npublic sealed class Q{i}Handler(ISender sender) : IRequestHandler<Q{i}, int> {{ public Task<int> Handle(Q{i} r, CancellationToken c) => {body}; }}\n");
            }

            trees.Add(CSharpSyntaxTree.ParseText(source.ToString(), parse, $"F{file}.cs"));
        }

        trees.Add(CSharpSyntaxTree.ParseText(
            "using Microsoft.Extensions.DependencyInjection; public static class Setup { public static IServiceCollection Add(IServiceCollection s) => s.AddRelevox(); }",
            parse,
            "Setup.cs"));
        CSharpCompilation compilation = CSharpCompilation.Create("Big", trees, GeneratorHarness.References, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        CSharpGeneratorDriver.Create([new RelevoxGenerator().AsSourceGenerator()], parseOptions: parse)
            .RunGeneratorsAndUpdateCompilation(compilation, out Compilation output, out _, TestContext.Current.CancellationToken);

        ImmutableArray<DiagnosticAnalyzer> analyzers = [new RelevoxAnalyzer(), new MediatRUsageAnalyzer(), new HandlerSendAnalyzer(), new BlockingCallAnalyzer(), new PublishLayerAnalyzer()];

        // Each analyzer alone, so that work Roslyn shares between analyzers, such as building operation trees, is
        // charged to every one that needs it rather than to whichever asked first. Three analyses each: the first
        // warms up the JIT, and the faster of the other two is reported.
        foreach (DiagnosticAnalyzer analyzer in analyzers)
        {
            List<double> times = [];
            for (int run = 0; run < 3; run++)
            {
                CompilationWithAnalyzers analysis = output.WithAnalyzers(
                    [analyzer],
                    new CompilationWithAnalyzersOptions(new AnalyzerOptions([]), onAnalyzerException: null, concurrentAnalysis: false, logAnalyzerExecutionTime: true));
                await analysis.GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);
                AnalyzerTelemetryInfo info = await analysis.GetAnalyzerTelemetryInfoAsync(analyzer, TestContext.Current.CancellationToken);
                if (run > 0)
                {
                    times.Add(info.ExecutionTime.TotalMilliseconds);
                }
            }

            string line = string.Create(CultureInfo.InvariantCulture, $"{analyzer.GetType().Name}: {times.Min():F0} ms");
            TestContext.Current.TestOutputHelper?.WriteLine(line);
            Console.WriteLine(line);
        }
    }
}
