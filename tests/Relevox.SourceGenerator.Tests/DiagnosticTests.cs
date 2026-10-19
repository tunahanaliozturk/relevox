using Microsoft.CodeAnalysis;

namespace Relevox.SourceGenerator.Tests;

public sealed class DiagnosticTests
{
    private const string Usings = "using Relevox; using System.Threading; using System.Threading.Tasks;";

    [Fact]
    public void A_request_without_a_handler_is_a_warning_and_the_rest_still_builds()
    {
        GeneratorRun run = GeneratorHarness.Run(Usings + """
            public sealed record Lonely : IRequest<int>;
            public sealed record Ping : IRequest<int>;
            public sealed class PingHandler : IRequestHandler<Ping, int>
            {
                public Task<int> Handle(Ping request, CancellationToken cancellationToken) => Task.FromResult(1);
            }
            """);

        Diagnostic diagnostic = run.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Id.ShouldBe("RVX0001");
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Warning);
        diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture).ShouldContain("Lonely");
        diagnostic.Location.GetLineSpan().Path.ShouldBe("File0.cs");
        run.CompilationErrors.ShouldBeEmpty();
    }

    [Fact]
    public void Diagnostics_point_into_source_so_a_pragma_suppresses_them()
    {
        // The reason the rules moved from the generator to an analyzer: a location the generator builds from a
        // path is outside the compilation, and a pragma around the declaration never reached it.
        GeneratorRun run = GeneratorHarness.Run(Usings + Environment.NewLine + """
            #pragma warning disable RVX0001 // Handled in another service, sent over the wire.
            public sealed record Lonely : IRequest<int>;
            #pragma warning restore RVX0001
            """);

        run.Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void A_diagnostic_is_reported_in_the_syntax_tree_it_is_about()
    {
        GeneratorRun run = GeneratorHarness.Run(Usings + "public sealed record Lonely : IRequest<int>;");

        Diagnostic diagnostic = run.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Location.IsInSource.ShouldBeTrue();
        diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan).ShouldBe("Lonely");
    }

    [Fact]
    public void Two_handlers_for_one_request_is_an_error_and_neither_is_dispatched()
    {
        GeneratorRun run = GeneratorHarness.Run(Usings + """
            public sealed record Ping : IRequest<int>;
            public sealed class First : IRequestHandler<Ping, int>
            {
                public Task<int> Handle(Ping request, CancellationToken cancellationToken) => Task.FromResult(1);
            }
            public sealed class Second : IRequestHandler<Ping, int>
            {
                public Task<int> Handle(Ping request, CancellationToken cancellationToken) => Task.FromResult(2);
            }
            """);

        Diagnostic diagnostic = run.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Id.ShouldBe("RVX0002");
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture).ShouldContain("First, Second");

        // Leaving it out keeps the user's error list to the one diagnostic that explains the problem, instead
        // of burying it under compiler errors pointing into generated code.
        run.Mediator.ShouldNotContain("Send(global::Ping request");
        run.CompilationErrors.ShouldBeEmpty();
    }

    [Fact]
    public void A_handler_the_generated_code_cannot_see_is_reported_and_skipped()
    {
        GeneratorRun run = GeneratorHarness.Run(Usings + """
            public sealed record Ping : IRequest<int>;
            public static class Outer
            {
                private sealed class Hidden : IRequestHandler<Ping, int>
                {
                    public Task<int> Handle(Ping request, CancellationToken cancellationToken) => Task.FromResult(1);
                }
            }
            """);

        run.Ids.Order().ShouldBe(["RVX0001", "RVX0003"]);
        run.CompilationErrors.ShouldBeEmpty();
    }

    [Fact]
    public void A_generic_handler_is_reported_rather_than_guessed_at()
    {
        GeneratorRun run = GeneratorHarness.Run(Usings + """
            public sealed record Ping : IRequest<int>;
            public sealed class Anything<T> : IRequestHandler<Ping, int>
            {
                public Task<int> Handle(Ping request, CancellationToken cancellationToken) => Task.FromResult(1);
            }
            """);

        run.Ids.Order().ShouldBe(["RVX0001", "RVX0004"]);
        run.CompilationErrors.ShouldBeEmpty();
    }

    [Fact]
    public void Several_notification_handlers_are_normal_and_reported_as_nothing()
    {
        GeneratorRun run = GeneratorHarness.Run(Usings + """
            public sealed record Happened : INotification;
            public sealed class A : INotificationHandler<Happened>
            {
                public Task Handle(Happened notification, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            public sealed class B : INotificationHandler<Happened>
            {
                public Task Handle(Happened notification, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """);

        run.Diagnostics.ShouldBeEmpty();
        run.CompilationErrors.ShouldBeEmpty();
    }

    [Fact]
    public void Every_rule_links_to_a_section_of_the_diagnostics_page_that_exists()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Relevox.slnx")))
        {
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Relevox.slnx not found above the test output.");
        }

        string page = File.ReadAllText(Path.Combine(root, "docs", "diagnostics.md"));

        foreach (DiagnosticDescriptor rule in Diagnostics.All.Append(Relevox.Migration.MediatRUsageAnalyzer.Rule))
        {
            rule.HelpLinkUri.ShouldEndWith("/docs/diagnostics.md#" + rule.Id.ToLowerInvariant());
            // A heading of the id alone, so its anchor is the lower-case id the link ends with.
            page.ReplaceLineEndings("\n").ShouldContain($"## {rule.Id}\n\n**{rule.Title}.**");
        }
    }

    [Fact]
    public void The_fix_the_diagnostics_page_gives_for_a_generic_handler_dispatches_both_closed_requests()
    {
        GeneratorRun run = GeneratorHarness.Run(Usings + """
            public sealed record Customer;
            public sealed record Order;
            public sealed record GetById<T>(int Id) : IRequest<T?>;
            public abstract class GetByIdHandler<T> : IRequestHandler<GetById<T>, T?>
            {
                public Task<T?> Handle(GetById<T> request, CancellationToken cancellationToken) => Task.FromResult(default(T));
            }
            public sealed class GetCustomerById : GetByIdHandler<Customer>;
            public sealed class GetOrderById : GetByIdHandler<Order>;
            """);

        run.Diagnostics.ShouldBeEmpty();
        run.Mediator.ShouldContain("GetById<global::Customer> request");
        run.Mediator.ShouldContain("GetById<global::Order> request");
        run.CompilationErrors.ShouldBeEmpty();
    }
}
