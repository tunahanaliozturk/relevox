using Microsoft.CodeAnalysis;

namespace Capar.SourceGenerator.Tests;

/// <summary>
/// Inputs that made the first version of the generator emit wrong or uncompilable code. Each test builds the
/// way a strict consumer does, with every warning an error and documentation comments checked.
/// </summary>
public sealed class EdgeCaseTests
{
    private const string Usings = "using Capar; using System; using System.Threading; using System.Threading.Tasks;\n";

    [Fact]
    public void Obsolete_and_experimental_types_do_not_break_a_warnings_as_errors_build()
    {
        GeneratorRun run = GeneratorHarness.RunStrict(Usings + """
            #pragma warning disable CS0618, CAPAR_PREVIEW
            [Obsolete("use V2")]
            public sealed record OldQuery : IRequest<int>;

            [Obsolete("use V2")]
            public sealed class OldQueryHandler : IRequestHandler<OldQuery, int>
            {
                public Task<int> Handle(OldQuery request, CancellationToken cancellationToken) => Task.FromResult(1);
            }

            [System.Diagnostics.CodeAnalysis.Experimental("CAPAR_PREVIEW")]
            public sealed record Preview : IRequest<int>;

            [System.Diagnostics.CodeAnalysis.Experimental("CAPAR_PREVIEW")]
            public sealed class PreviewHandler : IRequestHandler<Preview, int>
            {
                public Task<int> Handle(Preview request, CancellationToken cancellationToken) => Task.FromResult(2);
            }
            """);

        run.CompilationErrors.ShouldBeEmpty();
        run.Mediator.ShouldContain("#pragma warning disable CAPAR_PREVIEW");
    }

    [Fact]
    public void A_nullable_response_keeps_its_annotation()
    {
        GeneratorRun run = GeneratorHarness.RunStrict(Usings + """
            public sealed record Find(int Id) : IRequest<string?>;
            public sealed class FindHandler : IRequestHandler<Find, string?>
            {
                public Task<string?> Handle(Find request, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
            }
            """);

        run.CompilationErrors.ShouldBeEmpty();
        run.Mediator.ShouldContain("Task<string?> Send(global::Find request");
    }

    [Fact]
    public void A_closed_generic_notification_gets_a_dispatch_entry()
    {
        GeneratorRun run = GeneratorHarness.RunStrict(Usings + """
            public sealed record Order(int Id);
            public sealed record Created<T>(T Item) : INotification;
            public sealed class OrderCreated : INotificationHandler<Created<Order>>
            {
                public Task Handle(Created<Order> notification, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """);

        run.CompilationErrors.ShouldBeEmpty();
        run.Mediator.ShouldContain("typeof(global::Created<global::Order>)");
    }

    [Fact]
    public void A_struct_notification_does_not_reach_interface_catch_all_handlers()
    {
        GeneratorRun run = GeneratorHarness.RunStrict(Usings + """
            public readonly record struct Beep : INotification;
            public sealed class BeepHandler : INotificationHandler<Beep>
            {
                public Task Handle(Beep notification, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            public sealed class Everything : INotificationHandler<INotification>
            {
                public Task Handle(INotification notification, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """);

        run.CompilationErrors.ShouldBeEmpty();
        string publish = run.Mediator[run.Mediator.IndexOf("Publish(global::Beep notification", StringComparison.Ordinal)..];
        publish[..publish.IndexOf('}', StringComparison.Ordinal)].ShouldNotContain("INotificationHandler<global::Capar.INotification>");
    }

    [Fact]
    public void An_abstract_request_gets_no_typed_overload_and_an_unsealed_one_checks_its_runtime_type()
    {
        GeneratorRun run = GeneratorHarness.RunStrict(Usings + """
            public abstract record Shape : IRequest<int>;
            public sealed class ShapeHandler : IRequestHandler<Shape, int>
            {
                public Task<int> Handle(Shape request, CancellationToken cancellationToken) => Task.FromResult(1);
            }
            public record Open : IRequest<int>;
            public sealed class OpenHandler : IRequestHandler<Open, int>
            {
                public Task<int> Handle(Open request, CancellationToken cancellationToken) => Task.FromResult(2);
            }
            """);

        run.CompilationErrors.ShouldBeEmpty();
        run.Mediator.ShouldNotContain("Send(global::Shape request");
        run.Mediator.ShouldContain("if (request.GetType() != typeof(global::Open))");
    }

    [Fact]
    public void A_keyword_in_the_assembly_name_still_gives_a_valid_namespace()
    {
        GeneratorRun run = GeneratorHarness.Run("Acme.event.class", [], Usings + "public sealed record Ping : IRequest<int>;");

        run.Mediator.ShouldContain("namespace Acme.@event.@class");
        run.CompilationErrors.ShouldBeEmpty();
    }

    [Fact]
    public void A_handler_whose_project_does_not_reference_the_contracts_directly_is_still_found()
    {
        // The base handler lives in one project, the concrete handler in another that only names the base.
        // The compiler records no reference to Capar.Contracts in the second, so a direct-reference check
        // would skip it and the handler would silently be missing.
        MetadataReference framework = GeneratorHarness.Library("Shop.Framework", Usings + """
            namespace Shop.Framework;
            public abstract class CommandHandler<T> : IRequestHandler<T> where T : IRequest
            {
                public Task Handle(T request, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            public sealed record CreateOrder : IRequest;
            """);
        MetadataReference orders = GeneratorHarness.Library("Shop.Orders", [framework], withGenerator: false, """
            namespace Shop.Orders;
            public sealed class CreateOrderHandler : Shop.Framework.CommandHandler<Shop.Framework.CreateOrder>;
            """);

        GeneratorRun host = GeneratorHarness.Run("Shop.Api", [framework, orders], "public static class Program;");

        host.Sources["CaparServiceCollectionExtensions.g.cs"].ShouldContain("typeof(global::Shop.Orders.CreateOrderHandler)");
        host.CompilationErrors.ShouldBeEmpty();
    }

    [Fact]
    public void A_referenced_project_with_its_own_mediator_keeps_its_handlers()
    {
        MetadataReference module = GeneratorHarness.Library("Billing", [], withGenerator: true, Usings + """
            namespace Billing;
            public sealed record Charge : IRequest<int>;
            public sealed class ChargeHandler : IRequestHandler<Charge, int>
            {
                public Task<int> Handle(Charge request, CancellationToken cancellationToken) => Task.FromResult(1);
            }
            """);

        GeneratorRun host = GeneratorHarness.Run("Shop.Api", [module], "public static class Program;");

        host.Sources["CaparServiceCollectionExtensions.g.cs"].ShouldNotContain("ChargeHandler");
        Diagnostic owned = host.Diagnostics.ShouldHaveSingleItem();
        owned.Id.ShouldBe("CPR0005");
        owned.GetMessage(System.Globalization.CultureInfo.InvariantCulture).ShouldContain("Billing.ChargeHandler");
    }
}
