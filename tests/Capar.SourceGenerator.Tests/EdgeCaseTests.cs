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
    public void Processors_and_exception_handlers_produce_code_that_compiles_strictly()
    {
        GeneratorRun run = GeneratorHarness.RunStrict(Usings + """
            using Capar.Pipeline;

            public sealed record Pay(decimal Amount) : IRequest<string>;
            public sealed class PayHandler : IRequestHandler<Pay, string>
            {
                public Task<string> Handle(Pay request, CancellationToken cancellationToken) => Task.FromResult("paid");
            }

            public sealed record Refund : IRequest;
            public sealed class RefundHandler : IRequestHandler<Refund>
            {
                public Task Handle(Refund request, CancellationToken cancellationToken) => Task.CompletedTask;
            }

            public sealed class PayCheck : IRequestPreProcessor<Pay>
            {
                public Task Process(Pay request, CancellationToken cancellationToken) => Task.CompletedTask;
            }

            public sealed class Audit<TRequest> : IRequestPreProcessor<TRequest> where TRequest : notnull
            {
                public Task Process(TRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
            }

            public sealed class RefundDone : IRequestPostProcessor<Refund, Unit>
            {
                public Task Process(Refund request, Unit response, CancellationToken cancellationToken) => Task.CompletedTask;
            }

            public sealed class Declined : IRequestExceptionHandler<Pay, string, InvalidOperationException>
            {
                public Task Handle(Pay request, InvalidOperationException exception, RequestExceptionHandlerState<string> state, CancellationToken cancellationToken)
                {
                    state.SetHandled("declined");
                    return Task.CompletedTask;
                }
            }

            public sealed class Anything : IRequestExceptionHandler<Pay, string, Exception>
            {
                public Task Handle(Pay request, Exception exception, RequestExceptionHandlerState<string> state, CancellationToken cancellationToken) => Task.CompletedTask;
            }

            public sealed class Log : IRequestExceptionAction<Refund, Exception>
            {
                public Task Execute(Refund request, Exception exception, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """);

        run.CompilationErrors.ShouldBeEmpty();
        string registration = run.Sources["CaparServiceCollectionExtensions.g.cs"];
        registration.ShouldContain("typeof(global::Capar.Pipeline.IRequestExceptionHandler<global::Pay, string, global::System.InvalidOperationException>), typeof(global::Anything)");
        registration.ShouldContain("typeof(global::Capar.Pipeline.IRequestPreProcessor<>), typeof(global::Audit<>)");
        registration.ShouldContain("ExceptionCatalog<global::Refund, global::Capar.Unit>");
    }

    [Fact]
    public void A_generic_request_with_a_nullable_argument_and_an_exception_handler_compiles_strictly()
    {
        GeneratorRun run = GeneratorHarness.RunStrict(Usings + """
            using Capar.Pipeline;

            public sealed record Lookup<T>(T Key) : IRequest<string?>;
            public sealed class LookupHandler : IRequestHandler<Lookup<string?>, string?>
            {
                public Task<string?> Handle(Lookup<string?> request, CancellationToken cancellationToken) => Task.FromResult(request.Key);
            }

            public sealed class LookupFailed : IRequestExceptionHandler<Lookup<string?>, string?, Exception>
            {
                public Task Handle(Lookup<string?> request, Exception exception, RequestExceptionHandlerState<string?> state, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """);

        run.CompilationErrors.ShouldBeEmpty();
    }

    [Fact]
    public void Handlers_from_a_referenced_project_run_in_its_metadata_order()
    {
        // Declared Z first and A second, so name order and metadata order disagree.
        MetadataReference orders = GeneratorHarness.Library("Shop.Orders", Usings + """
            namespace Shop.Orders;
            public sealed record Placed : INotification;
            public sealed class ZAudit : INotificationHandler<Placed>
            {
                public Task Handle(Placed notification, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            public sealed class AMail : INotificationHandler<Placed>
            {
                public Task Handle(Placed notification, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """);

        GeneratorRun host = GeneratorHarness.Run("Shop.Api", [orders], "public static class Program;");

        // Handler fields are numbered by name, AMail 0 and ZAudit 1; the publish order is what matters.
        string mediator = host.Mediator;
        string publish = mediator[mediator.IndexOf("Publish(global::Shop.Orders.Placed notification", StringComparison.Ordinal)..];
        publish = publish[..publish.IndexOf("return ", StringComparison.Ordinal)];

        publish.ShouldContain("handler0 = Handler1();");
        publish.ShouldContain("handler1 = Handler0();");
        host.CompilationErrors.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("public static class Nothing;")]
    [InlineData("public sealed record Lonely : INotification;")]
    [InlineData("public sealed record Unheard : INotification; public sealed class Hears : INotificationHandler<Unheard> { public Task Handle(Unheard n, CancellationToken c) => Task.CompletedTask; }")]
    public void Small_projects_compile_strictly(string source)
    {
        GeneratorRun run = GeneratorHarness.RunStrict(Usings + source);

        run.CompilationErrors.ShouldBeEmpty();
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

        GeneratorRun host = GeneratorHarness.Run("Shop.Api", [module], """
            using Microsoft.Extensions.DependencyInjection;
            public static class Program
            {
                public static void Configure(IServiceCollection services) => services.AddCapar();
            }
            """);

        host.Sources["CaparServiceCollectionExtensions.g.cs"].ShouldNotContain("ChargeHandler");
        Diagnostic owned = host.Diagnostics.ShouldHaveSingleItem();
        owned.Id.ShouldBe("CPR0005");
        owned.GetMessage(System.Globalization.CultureInfo.InvariantCulture).ShouldContain("Billing.ChargeHandler");
    }

    [Fact]
    public void AddCapar_is_found_where_AddMediatR_was_with_no_using_for_the_project_namespace()
    {
        // Top-level statements live in the global namespace, so a method in the project's own namespace
        // was invisible to a Program.cs moved over from MediatR. Packing and installing the package found it.
        GeneratorRun run = GeneratorHarness.Run("Shop.Api", [], Usings + """
            using Microsoft.Extensions.DependencyInjection;
            public sealed record Ping : IRequest<int>;
            public sealed class PingHandler : IRequestHandler<Ping, int>
            {
                public Task<int> Handle(Ping request, CancellationToken cancellationToken) => Task.FromResult(1);
            }
            public static class Startup
            {
                public static IServiceCollection Configure(IServiceCollection services) => services.AddCapar();
            }
            """);

        run.Sources["CaparServiceCollectionExtensions.g.cs"].ShouldContain("namespace Microsoft.Extensions.DependencyInjection");
        run.Mediator.ShouldContain("namespace Shop.Api");
        run.CompilationErrors.ShouldBeEmpty();
    }

    [Fact]
    public void A_test_project_that_sees_the_app_internals_gets_its_own_AddCapar_without_an_ambiguous_call()
    {
        MetadataReference app = GeneratorHarness.Library("Shop.Api", [], withGenerator: true, Usings + """
            [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Shop.Tests")]
            namespace Shop.Api;
            public sealed record Ping : IRequest<int>;
            public sealed class PingHandler : IRequestHandler<Ping, int>
            {
                public Task<int> Handle(Ping request, CancellationToken cancellationToken) => Task.FromResult(1);
            }
            """);

        GeneratorRun tests = GeneratorHarness.Run("Shop.Tests", [app], Usings + """
            using Microsoft.Extensions.DependencyInjection;
            namespace Shop.Tests
            {
                public sealed record Probe : IRequest<int>;
                public sealed class ProbeHandler : IRequestHandler<Probe, int>
                {
                    public Task<int> Handle(Probe request, CancellationToken cancellationToken) => Task.FromResult(2);
                }
                public static class Own
                {
                    public static IServiceCollection Configure(IServiceCollection services) => services.AddCapar();
                }
            }
            public static class Outside
            {
                public static IServiceCollection Configure(IServiceCollection services) => services.AddCapar();
            }
            """);

        tests.Sources["CaparServiceCollectionExtensions.g.cs"].ShouldContain("namespace Shop.Tests");
        tests.CompilationErrors.ShouldBeEmpty();

        // Inside its own namespace the project reaches its own AddCapar. Outside it, the call binds to the app's,
        // which would register the app's handlers instead of these, so the analyzer says so and names the fix.
        // The app's public handler also earns CPR0005, which is right for a project that references Capar.
        Diagnostic foreign = tests.Diagnostics.Where(diagnostic => diagnostic.Id == "CPR0006").ShouldHaveSingleItem();
        foreign.Location.SourceTree!.GetText().ToString(foreign.Location.SourceSpan).ShouldBe("services.AddCapar()");
        string source = foreign.Location.SourceTree.ToString();
        int outside = source[..source.IndexOf("class Outside", StringComparison.Ordinal)].Count(character => character == '\n');
        foreign.Location.GetLineSpan().StartLinePosition.Line.ShouldBeGreaterThan(outside, "the call in Outside, not the one in Own");
        string message = foreign.GetMessage(System.Globalization.CultureInfo.InvariantCulture);
        message.ShouldContain("'Shop.Api'");
        message.ShouldContain("global::Shop.Tests.CaparServiceCollectionExtensions.AddCapar(services)");
    }

    [Fact]
    public void A_host_whose_call_binds_to_a_library_mediator_is_warned_rather_than_left_without_handlers()
    {
        // The library uses Capar privately, keeps its handlers internal so CPR0005 stays quiet, and grants the host
        // access. Without CPR0006 this built cleanly and failed only when the host sent its own request.
        MetadataReference library = GeneratorHarness.Library("Billing", [], withGenerator: true, Usings + """
            [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Shop.Api")]
            namespace Billing;
            internal sealed record Charge : IRequest<int>;
            internal sealed class ChargeHandler : IRequestHandler<Charge, int>
            {
                public Task<int> Handle(Charge request, CancellationToken cancellationToken) => Task.FromResult(1);
            }
            """);

        GeneratorRun host = GeneratorHarness.Run("Shop.Api", [library], Usings + """
            using Microsoft.Extensions.DependencyInjection;
            public sealed record Ping : IRequest<int>;
            public sealed class PingHandler : IRequestHandler<Ping, int>
            {
                public Task<int> Handle(Ping request, CancellationToken cancellationToken) => Task.FromResult(2);
            }
            public static class Startup
            {
                public static IServiceCollection Configure(IServiceCollection services) => services.AddCapar();
            }
            """);

        host.CompilationErrors.ShouldBeEmpty();
        Diagnostic foreign = host.Diagnostics.ShouldHaveSingleItem();
        foreign.Id.ShouldBe("CPR0006");
        foreign.GetMessage(System.Globalization.CultureInfo.InvariantCulture).ShouldContain("global::Shop.Api.CaparServiceCollectionExtensions.AddCapar(services)");
    }

    [Fact]
    public void A_project_downstream_of_the_one_with_the_mediator_writes_nothing_and_reports_nothing()
    {
        // Analyzers flow through project references, so an API or test project below the application layer runs
        // the generator too. Found migrating a real Clean Architecture solution, where every such project warned.
        MetadataReference application = GeneratorHarness.Library("Shop.Application", [], withGenerator: true, Usings + """
            using Microsoft.Extensions.DependencyInjection;
            namespace Shop.Application;
            public sealed record Ping : IRequest<int>;
            public sealed class PingHandler : IRequestHandler<Ping, int>
            {
                public Task<int> Handle(Ping request, CancellationToken cancellationToken) => Task.FromResult(1);
            }
            public static class Setup
            {
                public static IServiceCollection AddApplication(IServiceCollection services) => services.AddCapar();
            }
            """);

        GeneratorRun api = GeneratorHarness.Run("Shop.Api", [application], """
            public static class Program
            {
                public static void Configure(Microsoft.Extensions.DependencyInjection.IServiceCollection services) => Shop.Application.Setup.AddApplication(services);
            }
            """);

        api.Sources.ShouldBeEmpty();
        api.Diagnostics.ShouldBeEmpty();
        api.CompilationErrors.ShouldBeEmpty();
    }
}
