using Microsoft.CodeAnalysis;

namespace Capar.SourceGenerator.Tests;

public sealed class DiagnosticTests
{
    private const string Usings = "using Capar; using System.Threading; using System.Threading.Tasks;";

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
        diagnostic.Id.ShouldBe("CPR0001");
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
            #pragma warning disable CPR0001 // Handled in another service, sent over the wire.
            public sealed record Lonely : IRequest<int>;
            #pragma warning restore CPR0001
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
        diagnostic.Id.ShouldBe("CPR0002");
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

        run.Ids.Order().ShouldBe(["CPR0001", "CPR0003"]);
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

        run.Ids.Order().ShouldBe(["CPR0001", "CPR0004"]);
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
}
