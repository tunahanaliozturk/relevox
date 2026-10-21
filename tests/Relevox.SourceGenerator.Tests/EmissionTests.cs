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
    public void The_generated_code_compiles_as_CSharp_11()
    {
        // A project can pin an older LangVersion than its SDK's default. The generated files are compiled as part of
        // it, so they must not need anything newer than the oldest C# a supported project might pin.
        GeneratorRun run = GeneratorHarness.Run(Everything + """

            public sealed class Audit<T> : INotificationHandler<T> where T : INotification
            {
                public Task Handle(T notification, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """);
        Microsoft.CodeAnalysis.CSharp.CSharpParseOptions eleven = new(Microsoft.CodeAnalysis.CSharp.LanguageVersion.CSharp11);
        Microsoft.CodeAnalysis.Compilation older = run.Compilation.RemoveAllSyntaxTrees().AddSyntaxTrees(
            run.Compilation.SyntaxTrees.Select(tree => Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(tree.GetText(), eleven, tree.FilePath)));

        older.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .Select(static diagnostic => diagnostic.ToString())
            .ShouldBeEmpty();
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
    public void Span_names_and_attribute_values_are_constants_in_the_generated_code()
    {
        string mediator = GeneratorHarness.Run(Everything).Mediator;

        mediator.ShouldContain("StartSend(\"send PlaceOrder\", \"Shop.Orders.PlaceOrder\", \"Shop.Orders.PlaceOrderHandler\")");
        mediator.ShouldContain("StartSend(\"send CancelOrder\", \"Shop.Orders.CancelOrder\", \"Shop.Orders.CancelOrderHandler\")");
        mediator.ShouldContain("StartPublish(\"publish OrderPlaced\", \"Shop.Orders.OrderPlaced\", 1)");
        mediator.ShouldContain("StartStream(\"stream ListOrders\", \"Shop.Orders.ListOrders\", \"Shop.Orders.ListOrdersHandler\")");
    }

    [Fact]
    public void Generic_and_nested_names_are_written_the_way_the_runtime_names_a_type()
    {
        // A notification type the build never saw is named from Type.Name and Type.FullName at run time, so
        // these use CLR names too: Int32 rather than int, Nullable<Int32> rather than int?, a dot for nesting.
        GeneratorRun run = GeneratorHarness.Run("""
            using Relevox;
            using System.Threading;
            using System.Threading.Tasks;

            namespace Shop;

            public sealed record Paged<T>(T Item) : IRequest<string>;
            public sealed class PagedHandler : IRequestHandler<Paged<int?>, string>
            {
                public Task<string> Handle(Paged<int?> request, CancellationToken cancellationToken) => Task.FromResult("p");
            }

            public static class Catalog
            {
                public sealed record Lookup(int Id) : IRequest<string>;
                public sealed class LookupHandler : IRequestHandler<Lookup, string>
                {
                    public Task<string> Handle(Lookup request, CancellationToken cancellationToken) => Task.FromResult("l");
                }
            }
            """);

        run.Mediator.ShouldContain("StartSend(\"send Paged<Nullable<Int32>>\", \"Shop.Paged<System.Nullable<System.Int32>>\", \"Shop.PagedHandler\")");
        run.Mediator.ShouldContain("StartSend(\"send Lookup\", \"Shop.Catalog.Lookup\", \"Shop.Catalog.LookupHandler\")");
        run.CompilationErrors.ShouldBeEmpty();
    }

    [Fact]
    public void With_telemetry_off_a_send_reads_one_flag_before_the_direct_path()
    {
        string mediator = GeneratorHarness.Run(Everything).Mediator;
        string send = Between(mediator, "Send(global::Shop.Orders.PlaceOrder request", "private ");

        // The span, its try block and everything else live in SendTraced, which the direct path never reaches.
        send.ShouldContain("if (_telemetry)");
        send.ShouldNotContain("TelemetryScope");
        send.ShouldNotContain("catch");
    }

    [Fact]
    public void Handler_spans_live_in_paths_of_their_own_so_a_publish_without_them_does_nothing_extra_per_handler()
    {
        string mediator = GeneratorHarness.Run(Everything).Mediator;
        string untraced = Between(mediator, "private global::System.Threading.Tasks.Task PublishUntraced0(", "private global::System.Threading.Tasks.Task PublishWithHandlerSpans0(");
        string traced = Between(mediator, "private global::System.Threading.Tasks.Task PublishTraced0(", "[global::System.Runtime.CompilerServices.MethodImpl");

        // The option is read once per traced publish, never per handler, and never with telemetry off.
        untraced.ShouldNotContain("TelemetryScope");
        untraced.ShouldNotContain("_handlerSpans");
        traced.ShouldContain("_handlerSpans ? PublishWithHandlerSpans0(notification, cancellationToken) : PublishUntraced0(notification, cancellationToken)");
        mediator.ShouldContain("TelemetryScope.Handle<global::Shop.Orders.OrderPlaced>(\"handle OrderPlaced\", \"Shop.Orders.OrderPlaced\", \"Shop.Orders.SendReceipt\", handler0, notification!, cancellationToken)");
        mediator.ShouldContain("TelemetryScope.Handle<global::Shop.Orders.OrderPlaced>(\"handle OrderPlaced\", \"Shop.Orders.OrderPlaced\", \"Shop.Orders.SendReceipt\", handler0, ((global::Shop.Orders.OrderPlaced)message)!, token)");
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
