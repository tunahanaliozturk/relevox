using System.Globalization;
using Microsoft.CodeAnalysis;
using Relevox.Migration;

namespace Relevox.SourceGenerator.Tests;

/// <summary>RVX0008, a handler that sends another request, and RVX0009, handlers that send each other in a cycle (#48).</summary>
public sealed class HandlerSendTests
{
    private const string Usings = "using Relevox; using System.Collections.Generic; using System.Threading; using System.Threading.Tasks;\n";

    private const string Leaf = """
        public sealed record Leaf : IRequest<int>;
        public sealed class LeafHandler : IRequestHandler<Leaf, int>
        {
            public Task<int> Handle(Leaf request, CancellationToken cancellationToken) => Task.FromResult(1);
        }

        """;

    [Fact]
    public void A_send_through_ISender_inside_a_handler_is_reported_at_the_call()
    {
        Diagnostic diagnostic = Analyze(Leaf + """
            public sealed record Outer : IRequest<int>;
            public sealed class OuterHandler(ISender sender) : IRequestHandler<Outer, int>
            {
                public Task<int> Handle(Outer request, CancellationToken cancellationToken) => sender.Send(new Leaf(), cancellationToken);
            }
            """).ShouldHaveSingleItem();

        diagnostic.Id.ShouldBe("RVX0008");
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Info);
        diagnostic.GetMessage(CultureInfo.InvariantCulture).ShouldBe("'OuterHandler' sends 'Leaf' from inside a handler, so a second pipeline runs inside this one");
        Text(diagnostic).ShouldBe("Send");
    }

    [Fact]
    public void A_send_through_an_injected_IMediator_is_reported()
    {
        Analyze(Leaf + """
            public sealed record Outer : IRequest<int>;
            public sealed class OuterHandler : IRequestHandler<Outer, int>
            {
                private readonly IMediator _mediator;

                public OuterHandler(IMediator mediator) => _mediator = mediator;

                public async Task<int> Handle(Outer request, CancellationToken cancellationToken) =>
                    await _mediator.Send(request: new Leaf(), cancellationToken: cancellationToken) + 1;
            }
            """).ShouldHaveSingleItem().Id.ShouldBe("RVX0008");
    }

    [Fact]
    public void A_send_in_a_lambda_or_a_helper_method_of_the_handler_is_reported()
    {
        Analyze(Leaf + """
            public sealed record Outer : IRequest<int>;
            public sealed class OuterHandler(ISender sender) : IRequestHandler<Outer, int>
            {
                public Task<int> Handle(Outer request, CancellationToken cancellationToken)
                {
                    System.Func<Task<int>> later = () => sender.Send(new Leaf(), cancellationToken);
                    return Helper(later);
                }

                private Task<int> Helper(System.Func<Task<int>> later) => later().ContinueWith(_ => sender.Send(new Leaf())).Unwrap();
            }
            """).Select(static diagnostic => diagnostic.Id).ShouldBe(["RVX0008", "RVX0008"]);
    }

    [Fact]
    public void Notification_and_stream_handlers_that_send_are_reported_and_a_stream_created_in_a_handler_too()
    {
        Analyze(Leaf + """
            public sealed record Numbers : IStreamRequest<int>;
            public sealed class NumbersHandler : IStreamRequestHandler<Numbers, int>
            {
                public async IAsyncEnumerable<int> Handle(Numbers request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
                {
                    yield return 1;
                    await Task.CompletedTask;
                }
            }

            public sealed record Happened : INotification;
            public sealed class Listener(IMediator mediator) : INotificationHandler<Happened>
            {
                public async Task Handle(Happened notification, CancellationToken cancellationToken)
                {
                    await mediator.Send(new Leaf(), cancellationToken);
                    await foreach (int number in mediator.CreateStream(new Numbers(), cancellationToken))
                    {
                    }
                }
            }
            """).Select(static diagnostic => diagnostic.Id).ShouldBe(["RVX0008", "RVX0008"]);
    }

    [Fact]
    public void A_publish_from_a_handler_and_a_send_outside_any_handler_are_not_reported()
    {
        Analyze(Leaf + """
            public sealed record Happened : INotification;
            public sealed record Outer : IRequest;
            public sealed class OuterHandler(IMediator mediator) : IRequestHandler<Outer>
            {
                public Task Handle(Outer request, CancellationToken cancellationToken) => mediator.Publish(new Happened(), cancellationToken);
            }

            public sealed class Controller(ISender sender)
            {
                public Task<int> Get() => sender.Send(new Leaf());
            }
            """).ShouldBeEmpty();
    }

    [Fact]
    public void Two_handlers_that_send_each_other_are_a_cycle_at_both_sends()
    {
        Diagnostic[] cycles = [.. Analyze("""
            public sealed record Ping : IRequest<int>;
            public sealed record Pong : IRequest<int>;
            public sealed class PingHandler(ISender sender) : IRequestHandler<Ping, int>
            {
                public Task<int> Handle(Ping request, CancellationToken cancellationToken) => sender.Send(new Pong(), cancellationToken);
            }

            public sealed class PongHandler(ISender sender) : IRequestHandler<Pong, int>
            {
                public Task<int> Handle(Pong request, CancellationToken cancellationToken) => sender.Send(new Ping(), cancellationToken);
            }
            """).Where(static diagnostic => diagnostic.Id == "RVX0009")];

        cycles.Select(static diagnostic => diagnostic.GetMessage(CultureInfo.InvariantCulture)).ShouldBe(
        [
            "The handler of 'Ping' sends 'Pong', which leads back to 'Ping': Ping -> Pong -> Ping",
            "The handler of 'Pong' sends 'Ping', which leads back to 'Pong': Pong -> Ping -> Pong",
        ]);
        cycles.ShouldAllBe(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Info);
    }

    [Fact]
    public void Three_handlers_in_a_ring_are_a_cycle_and_a_chain_that_ends_is_not()
    {
        Diagnostic[] diagnostics = [.. Analyze("""
            public sealed record A : IRequest;
            public sealed record B : IRequest;
            public sealed record C : IRequest;
            public sealed record D : IRequest;
            public sealed class AHandler(ISender sender) : IRequestHandler<A>
            {
                public Task Handle(A request, CancellationToken cancellationToken) => sender.Send(new B(), cancellationToken);
            }

            public sealed class BHandler(ISender sender) : IRequestHandler<B>
            {
                public async Task Handle(B request, CancellationToken cancellationToken)
                {
                    await sender.Send(new C(), cancellationToken);
                    await sender.Send(new D(), cancellationToken);
                }
            }

            public sealed class CHandler(ISender sender) : IRequestHandler<C>
            {
                public Task Handle(C request, CancellationToken cancellationToken) => sender.Send(new A(), cancellationToken);
            }

            public sealed class DHandler : IRequestHandler<D>
            {
                public Task Handle(D request, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """)];

        diagnostics.Count(static diagnostic => diagnostic.Id == "RVX0008").ShouldBe(4);
        diagnostics.Where(static diagnostic => diagnostic.Id == "RVX0009").Select(static diagnostic => diagnostic.GetMessage(CultureInfo.InvariantCulture)).ShouldBe(
        [
            "The handler of 'A' sends 'B', which leads back to 'A': A -> B -> C -> A",
            "The handler of 'B' sends 'C', which leads back to 'B': B -> C -> A -> B",
            "The handler of 'C' sends 'A', which leads back to 'C': C -> A -> B -> C",
        ]);
    }

    [Fact]
    public void A_handler_that_sends_its_own_request_is_a_cycle()
    {
        Analyze("""
            public sealed record Walk(int Depth) : IRequest<int>;
            public sealed class WalkHandler(ISender sender) : IRequestHandler<Walk, int>
            {
                public Task<int> Handle(Walk request, CancellationToken cancellationToken) =>
                    request.Depth == 0 ? Task.FromResult(0) : sender.Send(new Walk(request.Depth - 1), cancellationToken);
            }
            """).Where(static diagnostic => diagnostic.Id == "RVX0009").ShouldHaveSingleItem()
            .GetMessage(CultureInfo.InvariantCulture).ShouldBe("The handler of 'Walk' sends 'Walk', which leads back to 'Walk': Walk -> Walk");
    }

    [Fact]
    public void In_a_class_that_handles_two_requests_a_send_counts_only_from_the_Handle_it_is_in()
    {
        Analyze("""
            public sealed record Ping : IRequest<int>;
            public sealed record Pong : IRequest<int>;
            public sealed class Both(ISender sender) : IRequestHandler<Ping, int>, IRequestHandler<Pong, int>
            {
                public Task<int> Handle(Ping request, CancellationToken cancellationToken) => sender.Send(new Pong(), cancellationToken);

                public Task<int> Handle(Pong request, CancellationToken cancellationToken) => Task.FromResult(2);
            }
            """).Where(static diagnostic => diagnostic.Id == "RVX0009").ShouldBeEmpty();
    }

    [Fact]
    public void Explicit_Handle_implementations_count_only_from_their_own_request()
    {
        Analyze("""
            public sealed record Ping : IRequest<int>;
            public sealed record Pong : IRequest<int>;
            public sealed class Both(ISender sender) : IRequestHandler<Ping, int>, IRequestHandler<Pong, int>
            {
                Task<int> IRequestHandler<Ping, int>.Handle(Ping request, CancellationToken cancellationToken) => sender.Send(new Pong(), cancellationToken);

                Task<int> IRequestHandler<Pong, int>.Handle(Pong request, CancellationToken cancellationToken) => Task.FromResult(2);
            }
            """).Select(static diagnostic => diagnostic.Id).ShouldBe(["RVX0008"]);
    }

    [Fact]
    public void A_send_from_the_Handle_of_a_notification_is_from_no_request()
    {
        Analyze("""
            public sealed record Ping : IRequest<int>;
            public sealed record Happened : INotification;
            public sealed class Both(ISender sender) : IRequestHandler<Ping, int>, INotificationHandler<Happened>
            {
                public Task<int> Handle(Ping request, CancellationToken cancellationToken) => Task.FromResult(1);

                public Task Handle(Happened notification, CancellationToken cancellationToken) => sender.Send(new Ping(), cancellationToken);
            }
            """).Select(static diagnostic => diagnostic.Id).ShouldBe(["RVX0008"]);
    }

    private static Diagnostic[] Analyze(string source) =>
        [.. GeneratorHarness.Analyze(new HandlerSendAnalyzer(), Usings + source)
            .OrderBy(static diagnostic => diagnostic.Location.SourceSpan.Start)
            .ThenBy(static diagnostic => diagnostic.Id, StringComparer.Ordinal)];

    private static string Text(Diagnostic diagnostic) =>
        diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan);
}
