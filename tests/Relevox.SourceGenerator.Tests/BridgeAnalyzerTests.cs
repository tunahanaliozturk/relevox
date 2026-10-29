using System.Globalization;
using Microsoft.CodeAnalysis;
using Relevox.Migration;

namespace Relevox.SourceGenerator.Tests;

/// <summary>RVX0014 to RVX0016, what is left on MediatR while the bridge runs both (#132, ADR 0022).</summary>
/// <remarks>
/// MediatR itself is not referenced by this project, because RVX0007 tests need it absent, so a stand-in with
/// MediatR's names is compiled here. The analyzer only goes by those names.
/// </remarks>
public sealed class BridgeAnalyzerTests
{
    private static readonly MetadataReference MediatR = GeneratorHarness.Library("MediatR", """
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;

        namespace MediatR
        {
            public interface IBaseRequest;
            public interface IRequest : IBaseRequest;
            public interface IRequest<out TResponse> : IBaseRequest;
            public interface INotification;
            public interface IStreamRequest<out TResponse>;
            public interface IRequestHandler<in TRequest> where TRequest : IRequest { Task Handle(TRequest request, CancellationToken cancellationToken); }
            public interface IRequestHandler<in TRequest, TResponse> where TRequest : IRequest<TResponse> { Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken); }
            public interface INotificationHandler<in TNotification> where TNotification : INotification { Task Handle(TNotification notification, CancellationToken cancellationToken); }
            public interface IStreamRequestHandler<in TRequest, out TResponse> where TRequest : IStreamRequest<TResponse> { IAsyncEnumerable<TResponse> Handle(TRequest request, CancellationToken cancellationToken); }
            public delegate Task<TResponse> RequestHandlerDelegate<TResponse>(CancellationToken t = default);
            public interface IPipelineBehavior<in TRequest, TResponse> where TRequest : notnull { Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken); }
        }

        namespace MediatR.Pipeline
        {
            public interface IRequestPreProcessor<in TRequest> where TRequest : notnull { Task Process(TRequest request, CancellationToken cancellationToken); }
        }
        """);

    private static readonly MetadataReference Bridge = GeneratorHarness.Library("Relevox.MediatRBridge", """
        namespace Microsoft.Extensions.DependencyInjection
        {
            public static class MediatRBridgeServiceCollectionExtensions
            {
                public static IServiceCollection AddMediatRBridge(this IServiceCollection services) => services;
            }
        }
        """);

    private const string Usings = "using System.Threading; using System.Threading.Tasks; using Microsoft.Extensions.DependencyInjection;\n";

    private const string Legacy = """
        public sealed record Ping : MediatR.IRequest<int>;
        public sealed class PingHandler : MediatR.IRequestHandler<Ping, int>
        {
            public Task<int> Handle(Ping request, CancellationToken cancellationToken) => Task.FromResult(1);
        }

        """;

    private const string Moved = """
        public sealed record Pong : MediatR.IRequest<int>, Relevox.IRequest<int>;
        public sealed class PongHandler : Relevox.IRequestHandler<Pong, int>
        {
            public Task<int> Handle(Pong request, CancellationToken cancellationToken) => Task.FromResult(2);
        }

        """;

    private const string MediatROnlyBehavior = """
        public sealed class Audit<TRequest, TResponse> : MediatR.IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
        {
            public Task<TResponse> Handle(TRequest request, MediatR.RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken) => next(cancellationToken);
        }

        """;

    private const string BothBehavior = """
        public sealed class Logging<TRequest, TResponse> : MediatR.IPipelineBehavior<TRequest, TResponse>, Relevox.IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
        {
            public Task<TResponse> Handle(TRequest request, MediatR.RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken) => next(cancellationToken);
            public Task<TResponse> Handle(TRequest request, Relevox.RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken) => next(cancellationToken);
        }

        """;

    private const string MediatROnlyProcessor = """
        public sealed class Stamp : MediatR.Pipeline.IRequestPreProcessor<Ping>
        {
            public Task Process(Ping request, CancellationToken cancellationToken) => Task.CompletedTask;
        }

        """;

    private const string Registration = """
        public static class Startup
        {
            public static void Configure(IServiceCollection services) => services.AddMediatRBridge();
        }
        """;

    [Fact]
    public void The_bridge_call_counts_what_is_left_on_MediatR()
    {
        Diagnostic diagnostic = Analyze([MediatR, Bridge], Legacy + Moved + MediatROnlyBehavior + BothBehavior + MediatROnlyProcessor + Registration)
            .Single(static diagnostic => diagnostic.Id == "RVX0014");

        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Info);
        diagnostic.GetMessage(CultureInfo.InvariantCulture).ShouldBe("Still on MediatR, in this project and the projects it references: handlers 1, behaviours 1, processors 1");
        diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan).ShouldBe("services.AddMediatRBridge()");
    }

    [Fact]
    public void MediatR_types_in_a_referenced_project_are_counted()
    {
        MetadataReference feature = GeneratorHarness.Library("Feature", [MediatR], withGenerator: false, Usings + Legacy + """
            public sealed class Audit<TRequest, TResponse> : MediatR.IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
            {
                public Task<TResponse> Handle(TRequest request, MediatR.RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken) => next(cancellationToken);
            }
            """);

        Analyze([MediatR, Bridge, feature], Moved + Registration)
            .Single(static diagnostic => diagnostic.Id == "RVX0014")
            .GetMessage(CultureInfo.InvariantCulture)
            .ShouldBe("Still on MediatR, in this project and the projects it references: handlers 1, behaviours 1, processors 0");
    }

    [Fact]
    public void With_no_handler_left_the_bridge_call_says_it_can_go()
    {
        Diagnostic diagnostic = Analyze([MediatR, Bridge], Moved + BothBehavior + Registration).ShouldHaveSingleItem();

        diagnostic.Id.ShouldBe("RVX0016");
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Info);
        diagnostic.GetMessage(CultureInfo.InvariantCulture).ShouldBe("No handler in this project or the projects it references is left on MediatR; remove AddMediatRBridge, AddMediatR and the MediatR package");
    }

    [Fact]
    public void A_MediatR_only_behaviour_or_processor_is_a_warning_where_the_bridge_is_in_use()
    {
        Diagnostic[] warnings = [.. Analyze([MediatR, Bridge], Legacy + Moved + MediatROnlyBehavior + BothBehavior + MediatROnlyProcessor)];

        warnings.Select(static diagnostic => diagnostic.GetMessage(CultureInfo.InvariantCulture)).ShouldBe(
        [
            "'Audit<TRequest, TResponse>' runs only for requests MediatR handles; requests moved to Relevox skip it. Implement Relevox's IPipelineBehavior as well and register it with AddRelevox.",
            "'Stamp' runs only for requests MediatR handles; requests moved to Relevox skip it. Implement Relevox's IRequestPreProcessor as well and register it with AddRelevox.",
        ]);
        warnings.ShouldAllBe(static diagnostic => diagnostic.Id == "RVX0015" && diagnostic.Severity == DiagnosticSeverity.Warning);
        warnings[0].Location.SourceTree!.GetText().ToString(warnings[0].Location.SourceSpan).ShouldBe("Audit");
    }

    [Fact]
    public void A_class_that_is_both_a_handler_and_a_behaviour_counts_as_both_and_is_warned_about()
    {
        Diagnostic[] diagnostics = Analyze([MediatR, Bridge], Moved + """
            public sealed record Ping : MediatR.IRequest<int>;
            public sealed class PingEverything : MediatR.IRequestHandler<Ping, int>, MediatR.IPipelineBehavior<Ping, int>
            {
                public Task<int> Handle(Ping request, CancellationToken cancellationToken) => Task.FromResult(1);
                public Task<int> Handle(Ping request, MediatR.RequestHandlerDelegate<int> next, CancellationToken cancellationToken) => next(cancellationToken);
            }

            """ + Registration);

        diagnostics.Select(static diagnostic => diagnostic.Id).ShouldBe(["RVX0015", "RVX0014"]);
        diagnostics[1].GetMessage(CultureInfo.InvariantCulture).ShouldBe("Still on MediatR, in this project and the projects it references: handlers 1, behaviours 1, processors 0");
    }

    [Fact]
    public void A_message_with_both_interfaces_means_the_bridge_is_in_use_in_a_project_that_does_not_reference_it()
    {
        Analyze([MediatR], Moved + MediatROnlyBehavior).ShouldHaveSingleItem().Id.ShouldBe("RVX0015");
    }

    [Fact]
    public void A_project_that_references_the_bridge_and_has_moved_nothing_gets_no_warning()
    {
        Analyze([MediatR, Bridge], Legacy + MediatROnlyBehavior + MediatROnlyProcessor).ShouldBeEmpty();
        Analyze([MediatR, Bridge], Legacy + MediatROnlyBehavior + """
            public sealed record Moved : Relevox.IRequest<int>;
            public sealed class MovedHandler : Relevox.IRequestHandler<Moved, int>
            {
                public Task<int> Handle(Moved request, CancellationToken cancellationToken) => Task.FromResult(4);
            }
            """).ShouldHaveSingleItem().Id.ShouldBe("RVX0015");
    }

    [Fact]
    public void Nothing_is_reported_without_the_bridge()
    {
        Analyze([MediatR], Legacy + MediatROnlyBehavior + MediatROnlyProcessor).ShouldBeEmpty();
        Analyze([], "public sealed record Pong : Relevox.IRequest<int>;").ShouldBeEmpty();
    }

    private static Diagnostic[] Analyze(MetadataReference[] references, string source) =>
        [.. GeneratorHarness.Analyze(new BridgeAnalyzer(), new Dictionary<string, string>(), references, Usings + source)
            .OrderBy(static diagnostic => diagnostic.Location.SourceSpan.Start)];
}
