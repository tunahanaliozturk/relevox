using System.Globalization;
using Microsoft.CodeAnalysis;
using Relevox.Migration;

namespace Relevox.SourceGenerator.Tests;

/// <summary>RVX0013, two behaviours that could run for one request with the same PipelineOrder (#98).</summary>
public sealed class PipelineOrderAnalyzerTests
{
    private const string Usings = "using Relevox; using System.Collections.Generic; using System.Threading; using System.Threading.Tasks;\n";

    private const string Requests = """
        public sealed record Ping : IRequest<int>;
        public sealed record Pong : IRequest<int>;

        """;

    [Fact]
    public void Two_open_behaviours_with_one_number_are_reported_at_the_second()
    {
        Diagnostic[] diagnostics = Analyze(Requests + """
            [PipelineOrder(1)]
            public sealed class Logging<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
            {
                public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken) => next(cancellationToken);
            }

            [PipelineOrder(1)]
            public sealed class Validation<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
            {
                public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken) => next(cancellationToken);
            }
            """);

        Diagnostic diagnostic = diagnostics.ShouldHaveSingleItem();
        diagnostic.Id.ShouldBe("RVX0013");
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Info);
        diagnostic.GetMessage(CultureInfo.InvariantCulture).ShouldBe("'Validation<TRequest, TResponse>' has the same PipelineOrder, 1, as 'Logging<TRequest, TResponse>', so the two run in the order they are registered");
        diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan).ShouldBe("PipelineOrder(1)");
    }

    [Fact]
    public void An_open_behaviour_meets_a_closed_one_and_two_closed_ones_meet_only_for_one_request()
    {
        Analyze(Requests + """
            [PipelineOrder(2)]
            public sealed class PingCache : IPipelineBehavior<Ping, int>
            {
                public Task<int> Handle(Ping request, RequestHandlerDelegate<int> next, CancellationToken cancellationToken) => next(cancellationToken);
            }

            [PipelineOrder(2)]
            public sealed class PongCache : IPipelineBehavior<Pong, int>
            {
                public Task<int> Handle(Pong request, RequestHandlerDelegate<int> next, CancellationToken cancellationToken) => next(cancellationToken);
            }

            [PipelineOrder(2)]
            public sealed class Audit<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
            {
                public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken) => next(cancellationToken);
            }
            """).Select(static diagnostic => diagnostic.GetMessage(CultureInfo.InvariantCulture)).ShouldBe(
        [
            "'Audit<TRequest, TResponse>' has the same PipelineOrder, 2, as 'PingCache', so the two run in the order they are registered",
        ]);
    }

    [Fact]
    public void Pipeline_and_stream_behaviours_different_numbers_and_unordered_behaviours_do_not_meet()
    {
        Analyze(Requests + """
            [PipelineOrder(1)]
            public sealed class Logging<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
            {
                public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken) => next(cancellationToken);
            }

            [PipelineOrder(1)]
            public sealed class StreamLogging<TRequest, TResponse> : IStreamPipelineBehavior<TRequest, TResponse> where TRequest : notnull
            {
                public IAsyncEnumerable<TResponse> Handle(TRequest request, StreamHandlerDelegate<TResponse> next, CancellationToken cancellationToken) => next();
            }

            [PipelineOrder(2)]
            public sealed class Validation<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
            {
                public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken) => next(cancellationToken);
            }

            public sealed class Unordered<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
            {
                public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken) => next(cancellationToken);
            }
            """).ShouldBeEmpty();
    }

    [Fact]
    public void A_behaviour_that_meets_one_in_a_referenced_project_is_reported_here()
    {
        MetadataReference shared = GeneratorHarness.Library("Shop.Shared", Usings + """
            [PipelineOrder(-1)]
            public sealed class Tracing<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
            {
                public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken) => next(cancellationToken);
            }
            """);

        Diagnostic[] diagnostics = [.. GeneratorHarness.Analyze(new PipelineOrderAnalyzer(), new Dictionary<string, string>(), [shared], Usings + Requests + """
            [PipelineOrder(-1)]
            public sealed class Metrics<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
            {
                public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken) => next(cancellationToken);
            }
            """)];

        diagnostics.ShouldHaveSingleItem().GetMessage(CultureInfo.InvariantCulture).ShouldBe("'Metrics<TRequest, TResponse>' has the same PipelineOrder, -1, as 'Tracing<TRequest, TResponse>', so the two run in the order they are registered");
    }

    private static Diagnostic[] Analyze(string source) =>
        [.. GeneratorHarness.Analyze(new PipelineOrderAnalyzer(), Usings + source)
            .OrderBy(static diagnostic => diagnostic.Location.SourceSpan.Start)];
}
