using Microsoft.CodeAnalysis;

namespace Relevox.SourceGenerator.Tests;

public sealed class EmissionTests
{
    private const string Everything = """
        using Relevox;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;

        namespace Shop.Orders;

        public sealed record PlaceOrder(string Sku) : IRequest<int>;
        public sealed class PlaceOrderHandler : IRequestHandler<PlaceOrder, int>
        {
            public Task<int> Handle(PlaceOrder request, CancellationToken cancellationToken) => Task.FromResult(1);
        }

        public sealed record CancelOrder(int Id) : IRequest;
        public sealed class CancelOrderHandler : IRequestHandler<CancelOrder>
        {
            public Task Handle(CancelOrder request, CancellationToken cancellationToken) => Task.CompletedTask;
        }

        public readonly record struct Price(string Sku) : IRequest<decimal>;
        public sealed class PriceHandler : IRequestHandler<Price, decimal>
        {
            public Task<decimal> Handle(Price request, CancellationToken cancellationToken) => Task.FromResult(9.99m);
        }

        public sealed record OrderPlaced(int Id) : INotification;
        public sealed class SendReceipt : INotificationHandler<OrderPlaced>
        {
            public Task Handle(OrderPlaced notification, CancellationToken cancellationToken) => Task.CompletedTask;
        }

        public sealed record ListOrders : IStreamRequest<int>;
        public sealed class ListOrdersHandler : IStreamRequestHandler<ListOrders, int>
        {
            public async IAsyncEnumerable<int> Handle(ListOrders request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
            {
                yield return 1;
                await Task.CompletedTask;
            }
        }

        /// <summary>Implements one contract explicitly, which the generated call has to cope with.</summary>
        public sealed class Explicit : IRequestHandler<Explicitly, string>
        {
            Task<string> IRequestHandler<Explicitly, string>.Handle(Explicitly request, CancellationToken cancellationToken) => Task.FromResult("x");
        }
        public sealed record Explicitly : IRequest<string>;
        """;

    [Fact]
    public void Every_kind_of_message_produces_code_that_compiles()
    {
        GeneratorRun run = GeneratorHarness.Run(Everything);

        run.Diagnostics.ShouldBeEmpty();
        run.CompilationErrors.ShouldBeEmpty();
        run.Sources.Keys.Order().ShouldBe(["RelevoxMediator.g.cs", "RelevoxServiceCollectionExtensions.g.cs"]);
    }

    [Fact]
    public void A_struct_request_is_not_boxed_to_be_null_checked()
    {
        string mediator = GeneratorHarness.Run(Everything).Mediator;
        string send = Between(mediator, "Send(global::Shop.Orders.Price request", "}");

        send.ShouldNotContain("ThrowIfNull");
    }

    [Fact]
    public void The_typed_path_returns_the_handler_task_without_awaiting_it()
    {
        string mediator = GeneratorHarness.Run(Everything).Mediator;
        string send = Between(mediator, "Send(global::Shop.Orders.PlaceOrder request", "catch");

        // A byte read decides the direct path, and the direct path hands back the handler's own task.
        send.ShouldContain("_pipelines.TryDirect(");
        send.ShouldContain("behaviors is null ? ((global::Relevox.IRequestHandler<global::Shop.Orders.PlaceOrder, int>)Handler");
        send.ShouldNotContain("await");
    }

    [Fact]
    public void The_namespace_comes_from_the_assembly_name_made_valid()
    {
        GeneratorRun run = GeneratorHarness.Run("My-App.2nd.Api", [], Everything);

        run.Mediator.ShouldContain("namespace My_App._2nd.Api");
        run.CompilationErrors.ShouldBeEmpty();
    }

    [Fact]
    public void Output_does_not_depend_on_the_order_files_are_visited_in()
    {
        string[] parts =
        [
            "using Relevox; using System.Threading; using System.Threading.Tasks; public sealed record A : IRequest<int>; public sealed class AHandler : IRequestHandler<A, int> { public Task<int> Handle(A r, CancellationToken c) => Task.FromResult(1); }",
            "using Relevox; using System.Threading; using System.Threading.Tasks; public sealed record B : IRequest<int>; public sealed class BHandler : IRequestHandler<B, int> { public Task<int> Handle(B r, CancellationToken c) => Task.FromResult(2); }",
            "using Relevox; using System.Threading; using System.Threading.Tasks; public sealed record N : INotification; public sealed class N1 : INotificationHandler<N> { public Task Handle(N n, CancellationToken c) => Task.CompletedTask; } public sealed class N2 : INotificationHandler<N> { public Task Handle(N n, CancellationToken c) => Task.CompletedTask; }",
        ];

        GeneratorRun forward = GeneratorHarness.Run(parts);
        GeneratorRun backward = GeneratorHarness.Run([.. parts.Reverse()]);

        backward.Mediator.ShouldBe(forward.Mediator);
        backward.Sources["RelevoxServiceCollectionExtensions.g.cs"].ShouldBe(forward.Sources["RelevoxServiceCollectionExtensions.g.cs"]);
    }

    [Fact]
    public void Handlers_in_a_referenced_project_are_found_and_internal_ones_are_reported()
    {
        MetadataReference application = GeneratorHarness.Library("Shop.Application", """
            using Relevox;
            using System.Threading;
            using System.Threading.Tasks;

            namespace Shop.Application;

            public sealed record GetStock(string Sku) : IRequest<int>;
            public sealed class GetStockHandler : IRequestHandler<GetStock, int>
            {
                public Task<int> Handle(GetStock request, CancellationToken cancellationToken) => Task.FromResult(3);
            }

            public sealed record Audit : IRequest;
            internal sealed class AuditHandler : IRequestHandler<Audit>
            {
                public Task Handle(Audit request, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """);

        GeneratorRun host = GeneratorHarness.Run("Shop.Api", [application], "public static class Program { }");

        host.Mediator.ShouldContain("Send(global::Shop.Application.GetStock request");
        host.Sources["RelevoxServiceCollectionExtensions.g.cs"].ShouldContain("typeof(global::Shop.Application.GetStockHandler)");

        Diagnostic inaccessible = host.Diagnostics.ShouldHaveSingleItem();
        inaccessible.Id.ShouldBe("RVX0003");
        inaccessible.GetMessage(System.Globalization.CultureInfo.InvariantCulture).ShouldContain("Shop.Application");
        host.CompilationErrors.ShouldBeEmpty();
    }

    [Fact]
    public void The_reference_scan_cache_keeps_InternalsVisibleTo_per_consumer()
    {
        // One reference object, two consumers. The scan of the reference is cached, and must not hand the
        // second consumer the first one's idea of what it can see.
        MetadataReference application = GeneratorHarness.Library("Shop.Application", """
            using Relevox;
            using System.Threading;
            using System.Threading.Tasks;

            [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Shop.Tests")]

            namespace Shop.Application;

            public sealed record Audit : IRequest;
            internal sealed class AuditHandler : IRequestHandler<Audit>
            {
                public Task Handle(Audit request, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """);

        GeneratorRun outsider = GeneratorHarness.Run("Shop.Api", [application], "public static class Program;");
        GeneratorRun friend = GeneratorHarness.Run("Shop.Tests", [application], "public static class Program;");
        GeneratorRun outsiderAgain = GeneratorHarness.Run("Shop.Api", [application], "public static class Program;");

        outsider.Ids.ShouldBe(["RVX0003"]);
        friend.Diagnostics.ShouldBeEmpty();
        friend.Sources["RelevoxServiceCollectionExtensions.g.cs"].ShouldContain("typeof(global::Shop.Application.AuditHandler)");
        outsiderAgain.Ids.ShouldBe(["RVX0003"]);
    }

    private static string Between(string text, string start, string end)
    {
        int from = text.IndexOf(start, StringComparison.Ordinal);
        from.ShouldBeGreaterThanOrEqualTo(0, $"'{start}' not found in generated code");
        int to = text.IndexOf(end, from, StringComparison.Ordinal);
        return text[from..to];
    }
}
