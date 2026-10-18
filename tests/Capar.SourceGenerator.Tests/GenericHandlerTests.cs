using Microsoft.CodeAnalysis;

namespace Capar.SourceGenerator.Tests;

/// <summary>
/// Generic handlers closed at build time. The behaviour MediatR shares is held to it by the differential
/// tests; these cover what only Capar does, and every reason a generic handler is reported instead.
/// </summary>
public sealed class GenericHandlerTests
{
    private const string Usings = "using Capar; using System; using System.Collections.Generic; using System.Threading; using System.Threading.Tasks;\n";

    private const string Stock = """
        public interface IStockItem;
        public sealed class Bolt : IStockItem;
        public sealed class Nut : IStockItem;
        public abstract class Fastener : IStockItem;
        public static class Holder
        {
            private sealed class Hidden : IStockItem;
        }
        """;

    private static string Reason(GeneratorRun run) =>
        run.Diagnostics.Where(diagnostic => diagnostic.Id == "CPR0004").ShouldHaveSingleItem().GetMessage(System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void A_generic_stream_handler_is_closed_over_every_type_its_constraint_admits()
    {
        // MediatR's RegisterGenericHandlers throws on this shape at registration, so there is no oracle for it.
        GeneratorRun run = GeneratorHarness.RunStrict(Usings + Stock + """
            public sealed record Feed<T>(int Count) : IStreamRequest<int> where T : IStockItem;
            public sealed class FeedHandler<T> : IStreamRequestHandler<Feed<T>, int> where T : IStockItem
            {
                public async IAsyncEnumerable<int> Handle(Feed<T> request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
                {
                    await Task.Yield();
                    yield return request.Count;
                }
            }
            """);

        run.Diagnostics.ShouldBeEmpty();
        run.Mediator.ShouldContain("global::FeedHandler<global::Bolt>");
        run.Mediator.ShouldContain("global::FeedHandler<global::Nut>");
        run.Mediator.ShouldNotContain("Fastener>", Case.Sensitive, "an abstract type can never be the runtime type argument of a sent request");
        run.Mediator.ShouldNotContain("Hidden", Case.Sensitive, "the generated code cannot name a private type");
        run.CompilationErrors.ShouldBeEmpty();
    }

    [Fact]
    public void Every_combination_of_two_constrained_type_parameters_is_closed()
    {
        GeneratorRun run = GeneratorHarness.Run(Usings + """
            public interface IUnit;
            public sealed class Metre : IUnit;
            public sealed class Foot : IUnit;
            public sealed record Convert<TFrom, TTo>(double Value) : IRequest<double> where TFrom : IUnit where TTo : IUnit;
            public sealed class ConvertHandler<TFrom, TTo> : IRequestHandler<Convert<TFrom, TTo>, double> where TFrom : IUnit where TTo : IUnit
            {
                public Task<double> Handle(Convert<TFrom, TTo> request, CancellationToken cancellationToken) => Task.FromResult(request.Value);
            }
            """);

        run.Diagnostics.ShouldBeEmpty();
        foreach (string closing in new[] { "<global::Metre, global::Metre>", "<global::Metre, global::Foot>", "<global::Foot, global::Metre>", "<global::Foot, global::Foot>" })
        {
            run.Mediator.ShouldContain("global::ConvertHandler" + closing);
        }

        run.CompilationErrors.ShouldBeEmpty();
    }

    [Fact]
    public void A_constraint_that_names_its_own_type_parameter_is_checked_per_candidate()
    {
        GeneratorRun run = GeneratorHarness.Run(Usings + """
            public interface IEntity<TSelf> where TSelf : IEntity<TSelf>;
            public sealed class Customer : IEntity<Customer>;
            public sealed class Impostor : IEntity<Customer>;
            public sealed record Load<T>(int Id) : IRequest<string> where T : IEntity<T>;
            public sealed class LoadHandler<T> : IRequestHandler<Load<T>, string> where T : IEntity<T>
            {
                public Task<string> Handle(Load<T> request, CancellationToken cancellationToken) => Task.FromResult(typeof(T).Name);
            }
            """);

        run.Diagnostics.ShouldBeEmpty();
        run.Mediator.ShouldContain("global::LoadHandler<global::Customer>");
        run.Mediator.ShouldNotContain("LoadHandler<global::Impostor>");
        run.CompilationErrors.ShouldBeEmpty();
    }

    [Fact]
    public void A_generic_notification_handler_in_a_referenced_project_is_closed_over_this_project_notifications()
    {
        MetadataReference audit = GeneratorHarness.Library("Shop.Audit", Usings + """
            namespace Shop.Audit;
            public sealed class Audit<T> : INotificationHandler<T> where T : INotification
            {
                public Task Handle(T notification, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """);

        GeneratorRun host = GeneratorHarness.Run("Shop.Api", [audit], Usings + """
            public sealed record Shipped : INotification;
            """);

        host.Diagnostics.ShouldBeEmpty();
        host.Mediator.ShouldContain("global::Shop.Audit.Audit<global::Shipped>");
        host.CompilationErrors.ShouldBeEmpty();
    }

    [Fact]
    public void An_unconstrained_type_parameter_is_reported_because_there_is_nothing_to_close_it_over()
    {
        GeneratorRun run = GeneratorHarness.Run(Usings + """
            public sealed record Wrap<T>(T Value) : IRequest<string>;
            public sealed class WrapHandler<T> : IRequestHandler<Wrap<T>, string>
            {
                public Task<string> Handle(Wrap<T> request, CancellationToken cancellationToken) => Task.FromResult("wrapped");
            }
            """);

        Reason(run).ShouldContain("type parameter 'T' has no constraint naming a type declared in this solution");
        run.CompilationErrors.ShouldBeEmpty();
    }

    [Fact]
    public void A_constraint_from_the_framework_does_not_bound_the_search()
    {
        GeneratorRun run = GeneratorHarness.Run(Usings + """
            public sealed record Compare<T>(T Value) : IRequest<int> where T : IComparable<T>;
            public sealed class CompareHandler<T> : IRequestHandler<Compare<T>, int> where T : IComparable<T>
            {
                public Task<int> Handle(Compare<T> request, CancellationToken cancellationToken) => Task.FromResult(0);
            }
            """);

        Reason(run).ShouldContain("no constraint naming a type declared in this solution");
    }

    [Fact]
    public void A_constraint_nothing_satisfies_is_reported()
    {
        GeneratorRun run = GeneratorHarness.Run(Usings + """
            public interface IArchived;
            public sealed record Restore<T>(int Id) : IRequest<bool> where T : IArchived;
            public sealed class RestoreHandler<T> : IRequestHandler<Restore<T>, bool> where T : IArchived
            {
                public Task<bool> Handle(Restore<T> request, CancellationToken cancellationToken) => Task.FromResult(true);
            }
            """);

        Reason(run).ShouldContain("no type in this solution satisfies its constraints");
    }

    [Fact]
    public void Too_many_closings_are_reported_rather_than_generated()
    {
        string items = string.Concat(Enumerable.Range(0, 300).Select(index => $"public sealed class Item{index} : IStockItem;\n"));
        GeneratorRun run = GeneratorHarness.Run(Usings + "public interface IStockItem;\n" + items + """
            public sealed record Price<T>(int Id) : IRequest<decimal> where T : IStockItem;
            public sealed class PriceHandler<T> : IRequestHandler<Price<T>, decimal> where T : IStockItem
            {
                public Task<decimal> Handle(Price<T> request, CancellationToken cancellationToken) => Task.FromResult(1m);
            }
            """);

        Reason(run).ShouldContain("satisfied by 300 type combinations, more than the 256");
        run.Mediator.ShouldNotContain("PriceHandler<");
    }

    [Fact]
    public void A_notification_handler_that_is_not_shaped_like_its_interface_is_reported()
    {
        GeneratorRun run = GeneratorHarness.Run(Usings + """
            public sealed record Envelope<T>(T Body) : INotification;
            public sealed class Opener<T> : INotificationHandler<Envelope<T>>
            {
                public Task Handle(Envelope<T> notification, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """);

        Reason(run).ShouldContain("declared as H<T> : INotificationHandler<T>");
    }

    [Fact]
    public void A_handler_nested_in_a_generic_type_is_reported_and_the_rest_still_builds()
    {
        GeneratorRun run = GeneratorHarness.Run(Usings + """
            public sealed record Ping : IRequest<int>;
            public static class Outer<T>
            {
                public sealed class PingHandler : IRequestHandler<Ping, int>
                {
                    public Task<int> Handle(Ping request, CancellationToken cancellationToken) => Task.FromResult(1);
                }
            }
            """);

        run.Ids.Order().ShouldBe(["CPR0001", "CPR0004"]);
        Reason(run).ShouldContain("nested in a generic type");
        run.CompilationErrors.ShouldBeEmpty();
    }

    [Fact]
    public void A_written_handler_for_one_closing_wins_without_an_ambiguity_error()
    {
        GeneratorRun run = GeneratorHarness.Run(Usings + Stock + """
            public sealed record Count<T> : IRequest<int> where T : IStockItem;
            public sealed class CountHandler<T> : IRequestHandler<Count<T>, int> where T : IStockItem
            {
                public Task<int> Handle(Count<T> request, CancellationToken cancellationToken) => Task.FromResult(0);
            }
            public sealed class BoltCount : IRequestHandler<Count<Bolt>, int>
            {
                public Task<int> Handle(Count<Bolt> request, CancellationToken cancellationToken) => Task.FromResult(1);
            }
            """);

        run.Diagnostics.ShouldBeEmpty();
        run.Mediator.ShouldContain("global::BoltCount");
        run.Mediator.ShouldNotContain("global::CountHandler<global::Bolt>");
        run.Mediator.ShouldContain("global::CountHandler<global::Nut>");
        run.CompilationErrors.ShouldBeEmpty();
    }
}
