using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Relevox.CodeFixes;
using Relevox.Migration;

namespace Relevox.SourceGenerator.Tests;

/// <summary>
/// The code fix that moves Mediator (martinothamar/Mediator 3.0.2) code to Relevox, run against code written for
/// Mediator with its package already gone, which is exactly the state after removing it. No stand-in assembly
/// is needed for that: nothing of Mediator's resolves, which is the situation the fix is written for.
/// </summary>
/// <remarks>
/// Every test runs one "Fix all in solution" started on RVX0017, as the IDE does, and the result then has to
/// build with the generator, so a fix that leaves code which does not compile, or needs a second run, cannot
/// pass. The few shapes that cannot be moved say so by asserting the compile error that is left.
/// </remarks>
public sealed class MediatorMigrationCodeFixTests
{
    // Sorted the way the IDE sorts, so every test also shows the usings staying in order.
    private const string Header = """
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using Mediator;
        using Microsoft.Extensions.DependencyInjection;

        namespace Shop;

        """;

    public static TheoryData<string, string, string[]> Rows => new()
    {
        {
            "IRequest<T> and IRequestHandler<TReq, T>",
            """
            public sealed record Ping(int Value) : IRequest<int>;
            public sealed class PingHandler : IRequestHandler<Ping, int>
            {
                public ValueTask<int> Handle(Ping request, CancellationToken cancellationToken) => new(request.Value);
            }
            """,
            ["record Ping(int Value) : IRequest<int>;", "IRequestHandler<Ping, int>", "public Task<int> Handle(Ping request, CancellationToken cancellationToken) => Task.FromResult(request.Value);"]
        },
        {
            "ICommand<T> and ICommandHandler<TReq, T>",
            """
            public sealed record Place(int Value) : ICommand<int>;
            public sealed class PlaceHandler : ICommandHandler<Place, int>
            {
                public ValueTask<int> Handle(Place command, CancellationToken cancellationToken) => new(command.Value);
            }
            """,
            ["record Place(int Value) : IRequest<int>;", "PlaceHandler : IRequestHandler<Place, int>", "Task<int> Handle(Place command"]
        },
        {
            "IQuery<T> and IQueryHandler<TReq, T>",
            """
            public sealed record Find(int Value) : IQuery<int>;
            public sealed class FindHandler : IQueryHandler<Find, int>
            {
                public ValueTask<int> Handle(Find query, CancellationToken cancellationToken) => ValueTask.FromResult(query.Value);
            }
            """,
            ["record Find(int Value) : IRequest<int>;", "FindHandler : IRequestHandler<Find, int>", "=> Task.FromResult(query.Value);"]
        },
        {
            "IRequest and IRequestHandler<TReq>",
            """
            public sealed record Ring : IRequest;
            public sealed class RingHandler : IRequestHandler<Ring>
            {
                public async ValueTask<Unit> Handle(Ring request, CancellationToken cancellationToken)
                {
                    await Task.Yield();
                    return Unit.Value;
                }
            }
            """,
            ["record Ring : IRequest<Unit>;", "RingHandler : IRequestHandler<Ring, Unit>", "public async Task<Unit> Handle(Ring request", "return Unit.Value;"]
        },
        {
            "ICommand and ICommandHandler<TReq>",
            """
            public sealed record Stop : ICommand;
            public sealed class StopHandler : ICommandHandler<Stop>
            {
                public ValueTask<Unit> Handle(Stop command, CancellationToken cancellationToken) => Unit.ValueTask;
            }
            """,
            ["record Stop : IRequest<Unit>;", "StopHandler : IRequestHandler<Stop, Unit>", "Task<Unit> Handle(Stop command, CancellationToken cancellationToken) => Task.FromResult(Unit.Value);"]
        },
        {
            "IStreamRequest<T> and IStreamRequestHandler<TReq, T> need only the namespace",
            """
            public sealed record Numbers(int Count) : IStreamRequest<int>;
            public sealed class NumbersHandler : IStreamRequestHandler<Numbers, int>
            {
                public async IAsyncEnumerable<int> Handle(Numbers request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
                {
                    await Task.Yield();
                    yield return request.Count;
                }
            }
            """,
            ["record Numbers(int Count) : IStreamRequest<int>;", "IStreamRequestHandler<Numbers, int>", "using Relevox;"]
        },
        {
            "IStreamCommand<T> and IStreamCommandHandler<TReq, T>",
            """
            public sealed record Count(int Up) : IStreamCommand<int>;
            public sealed class CountHandler : IStreamCommandHandler<Count, int>
            {
                public async IAsyncEnumerable<int> Handle(Count command, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
                {
                    await Task.Yield();
                    yield return command.Up;
                }
            }
            """,
            ["record Count(int Up) : IStreamRequest<int>;", "CountHandler : IStreamRequestHandler<Count, int>"]
        },
        {
            "IStreamQuery<T> and IStreamQueryHandler<TReq, T>",
            """
            public sealed record Scan(int Up) : IStreamQuery<int>;
            public sealed class ScanHandler : IStreamQueryHandler<Scan, int>
            {
                public async IAsyncEnumerable<int> Handle(Scan query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
                {
                    await Task.Yield();
                    yield return query.Up;
                }
            }
            """,
            ["record Scan(int Up) : IStreamRequest<int>;", "ScanHandler : IStreamRequestHandler<Scan, int>"]
        },
        {
            "INotification and INotificationHandler<T>",
            """
            public sealed record Hello : INotification;
            public sealed class HelloHandler : INotificationHandler<Hello>
            {
                public ValueTask Handle(Hello notification, CancellationToken cancellationToken) => ValueTask.CompletedTask;
            }
            """,
            ["record Hello : INotification;", "INotificationHandler<Hello>", "public Task Handle(Hello notification, CancellationToken cancellationToken) => Task.CompletedTask;"]
        },
        {
            "IPipelineBehavior<TMessage, T>",
            """
            public sealed class Logging<TMessage, TResponse> : IPipelineBehavior<TMessage, TResponse> where TMessage : IMessage
            {
                public ValueTask<TResponse> Handle(TMessage message, MessageHandlerDelegate<TMessage, TResponse> next, CancellationToken cancellationToken) => next(message, cancellationToken);
            }
            """,
            ["public Task<TResponse> Handle(TMessage message, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken) => next(cancellationToken);", "where TMessage : notnull"]
        },
        {
            "IStreamPipelineBehavior<TMessage, T>",
            """
            public sealed class Tap<TMessage, TResponse> : IStreamPipelineBehavior<TMessage, TResponse> where TMessage : IMessage
            {
                public IAsyncEnumerable<TResponse> Handle(TMessage message, StreamHandlerDelegate<TMessage, TResponse> next, CancellationToken cancellationToken) => next(message, cancellationToken);
            }
            """,
            ["public IAsyncEnumerable<TResponse> Handle(TMessage message, StreamHandlerDelegate<TResponse> next, CancellationToken cancellationToken) => next();", "where TMessage : notnull"]
        },
    };

    [Theory]
    [MemberData(nameof(Rows))]
    public async Task Each_row_of_the_mapping_table_is_rewritten_and_the_result_builds(string row, string source, string[] expected)
    {
        string[] fixedSources = await Migrate(Header + source);

        foreach (string text in expected)
        {
            fixedSources[0].ShouldContain(text, customMessage: row);
        }

        fixedSources[0].ShouldNotContain("Mediator", customMessage: row);
        fixedSources[0].ShouldContain("using Relevox;");
        ShouldBuildWithTheGenerator(fixedSources);
    }

    [Fact]
    public async Task A_void_command_keeps_Unit_and_its_handler_keeps_return_Unit_Value()
    {
        string[] fixedSources = await Migrate(Header + """
            public sealed record Ring : ICommand;
            public sealed class RingHandler : ICommandHandler<Ring>
            {
                public async ValueTask<Unit> Handle(Ring command, CancellationToken cancellationToken)
                {
                    await Task.Yield();
                    return Unit.Value;
                }
            }

            public sealed class Caller(ISender sender)
            {
                public async Task<Unit> Run(CancellationToken cancellationToken) => await sender.Send(new Ring(), cancellationToken);
            }
            """);

        fixedSources[0].ShouldContain("record Ring : IRequest<Unit>;");
        fixedSources[0].ShouldContain("return Unit.Value;");
        ShouldBuildWithTheGenerator(fixedSources);
    }

    [Fact]
    public async Task Every_completed_return_listed_in_the_ADR_is_rewritten_in_a_handler_that_is_not_async()
    {
        string[] fixedSources = await Migrate(Header + """
            public sealed record One(int V) : IRequest<int>;
            public sealed record Two(int V) : IRequest<int>;
            public sealed record Three(int V) : IRequest<int>;
            public sealed record Four(int V) : IRequest<int>;
            public sealed record Five : INotification;
            public sealed record Six : INotification;
            public sealed record Seven : ICommand;

            public sealed class OneHandler : IRequestHandler<One, int>
            {
                public ValueTask<int> Handle(One request, CancellationToken cancellationToken) => new ValueTask<int>(request.V);
            }

            public sealed class TwoHandler : IRequestHandler<Two, int>
            {
                public ValueTask<int> Handle(Two request, CancellationToken cancellationToken) => new(request.V);
            }

            public sealed class ThreeHandler : IRequestHandler<Three, int>
            {
                public ValueTask<int> Handle(Three request, CancellationToken cancellationToken) => ValueTask.FromResult(request.V);
            }

            public sealed class FourHandler : IRequestHandler<Four, int>
            {
                public ValueTask<int> Handle(Four request, CancellationToken cancellationToken)
                {
                    if (request.V > 0)
                    {
                        return new(1);
                    }
                    else
                    {
                        return ValueTask.FromResult<int>(2);
                    }
                }
            }

            public sealed class FiveHandler : INotificationHandler<Five>
            {
                public ValueTask Handle(Five notification, CancellationToken cancellationToken)
                {
                    return ValueTask.CompletedTask;
                }
            }

            public sealed class SixHandler : INotificationHandler<Six>
            {
                public ValueTask Handle(Six notification, CancellationToken cancellationToken)
                {
                    return default;
                }
            }

            public sealed class SevenHandler : ICommandHandler<Seven>
            {
                public ValueTask<Unit> Handle(Seven command, CancellationToken cancellationToken)
                {
                    return Mediator.Unit.ValueTask;
                }
            }
            """);

        string text = fixedSources[0];
        text.ShouldContain("Task<int> Handle(One request, CancellationToken cancellationToken) => Task.FromResult(request.V);");
        text.ShouldContain("Task<int> Handle(Two request, CancellationToken cancellationToken) => Task.FromResult(request.V);");
        text.ShouldContain("Task<int> Handle(Three request, CancellationToken cancellationToken) => Task.FromResult(request.V);");
        text.ShouldContain("return Task.FromResult(1);");
        text.ShouldContain("return Task.FromResult<int>(2);");
        text.ShouldContain("return Task.CompletedTask;");
        text.ShouldContain("return Task.FromResult(Relevox.Unit.Value);");
        text.ShouldNotContain("ValueTask");
        text.ShouldNotContain("default");
        ShouldBuildWithTheGenerator(fixedSources);
    }

    [Fact]
    public async Task A_default_return_becomes_a_completed_task_with_the_default_result()
    {
        string[] fixedSources = await Migrate(Header + """
            public sealed record Quiet : INotification;
            public sealed record Loud(int V) : IRequest<int>;
            public sealed record Named(int V) : IRequest<string>;

            public sealed class QuietHandler : INotificationHandler<Quiet>
            {
                public ValueTask Handle(Quiet notification, CancellationToken cancellationToken) => default;
            }

            public sealed class LoudHandler : IRequestHandler<Loud, int>
            {
                public ValueTask<int> Handle(Loud request, CancellationToken cancellationToken) => default;
            }

            public sealed class NamedHandler : IRequestHandler<Named, string>
            {
                public ValueTask<string> Handle(Named request, CancellationToken cancellationToken)
                {
                    if (request.V > 0)
                    {
                        return default(ValueTask<string>);
                    }

                    return default;
                }
            }
            """);

        fixedSources[0].ShouldContain("Task Handle(Quiet notification, CancellationToken cancellationToken) => Task.CompletedTask;");

        // default(ValueTask<T>) is completed with default(T). Left as default it would be a null Task<T>, which
        // compiles without nullable analysis and throws where it is awaited.
        fixedSources[0].ShouldContain("Task<int> Handle(Loud request, CancellationToken cancellationToken) => Task.FromResult<int>(default!);");
        fixedSources[0].ShouldContain("return Task.FromResult<string>(default!);");
        fixedSources[0].ShouldNotContain("return default");
        BuildErrors(fixedSources).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_name_through_an_alias_of_Mediator_is_moved()
    {
        string[] fixedSources = await Migrate("""
            using System.Threading;
            using System.Threading.Tasks;
            using M = Mediator;

            namespace Shop;

            public sealed record Ping(int V) : M.ICommand<int>;
            public sealed class PingHandler : M.ICommandHandler<Ping, int>
            {
                public ValueTask<int> Handle(Ping command, CancellationToken cancellationToken) => new(command.V);
            }
            """);

        fixedSources[0].ShouldContain("using M = Relevox;");
        fixedSources[0].ShouldContain("record Ping(int V) : Relevox.IRequest<int>;");
        fixedSources[0].ShouldContain("PingHandler : Relevox.IRequestHandler<Ping, int>");
        fixedSources[0].ShouldContain("=> Task.FromResult(command.V);");
        BuildErrors(fixedSources).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_returned_expression_the_ADR_does_not_list_is_left_for_the_compiler_and_the_build_says_so()
    {
        string[] fixedSources = await Migrate(Header + """
            public sealed record Ping(int V) : IRequest<int>;
            public sealed class PingHandler : IRequestHandler<Ping, int>
            {
                public ValueTask<int> Handle(Ping request, CancellationToken cancellationToken) => Compute(request.V);

                private static ValueTask<int> Compute(int value) => new(value);
            }
            """);

        fixedSources[0].ShouldContain("public Task<int> Handle(Ping request, CancellationToken cancellationToken) => Compute(request.V);");

        // The call is the only error: it needs .AsTask(), which only its type, gone with the package, could tell.
        BuildErrors(fixedSources).Select(static error => error.Id).Distinct().ShouldBe(["CS0029"]);
    }

    [Fact]
    public async Task An_async_handler_is_only_retyped()
    {
        string[] fixedSources = await Migrate(Header + """
            public sealed record Ping(int V) : IRequest<int>;
            public sealed class PingHandler : IRequestHandler<Ping, int>
            {
                public async ValueTask<int> Handle(Ping request, CancellationToken cancellationToken)
                {
                    await Task.Yield();
                    return request.V;
                }
            }

            public sealed record Hello : INotification;
            public sealed class HelloHandler : INotificationHandler<Hello>
            {
                public async ValueTask Handle(Hello notification, CancellationToken cancellationToken) => await Task.Yield();
            }
            """);

        fixedSources[0].ShouldContain("public async Task<int> Handle(Ping request, CancellationToken cancellationToken)");
        fixedSources[0].ShouldContain("await Task.Yield();\n        return request.V;");
        fixedSources[0].ShouldContain("public async Task Handle(Hello notification, CancellationToken cancellationToken) => await Task.Yield();");
        ShouldBuildWithTheGenerator(fixedSources);
    }

    [Fact]
    public async Task A_pipeline_behaviour_loses_the_message_in_next_and_the_IMessage_constraint()
    {
        string[] fixedSources = await Migrate(Header + """
            public sealed class Timing<TMessage, TResponse> : IPipelineBehavior<TMessage, TResponse>
                where TMessage : IMessage
            {
                public async ValueTask<TResponse> Handle(TMessage message, MessageHandlerDelegate<TMessage, TResponse> next, CancellationToken cancellationToken)
                {
                    TResponse response = await next(message, cancellationToken);
                    return response;
                }
            }
            """);

        fixedSources[0].ShouldContain("public async Task<TResponse> Handle(TMessage message, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)");
        fixedSources[0].ShouldContain("TResponse response = await next(cancellationToken);");
        fixedSources[0].ShouldContain("    where TMessage : notnull");
        fixedSources[0].ShouldNotContain("IMessage");
        ShouldBuildWithTheGenerator(fixedSources);
    }

    [Fact]
    public async Task The_IMessage_constraint_is_dropped_from_a_list_with_other_constraints_and_beside_notnull()
    {
        string[] fixedSources = await Migrate(Header + """
            public sealed class First<TMessage, TResponse> : IPipelineBehavior<TMessage, TResponse> where TMessage : class, IMessage
            {
                public ValueTask<TResponse> Handle(TMessage message, MessageHandlerDelegate<TMessage, TResponse> next, CancellationToken cancellationToken) => next(message, cancellationToken);
            }

            public sealed class Second<TMessage, TResponse> : IPipelineBehavior<TMessage, TResponse> where TMessage : notnull, IMessage
            {
                public ValueTask<TResponse> Handle(TMessage message, MessageHandlerDelegate<TMessage, TResponse> next, CancellationToken cancellationToken) => next(message, cancellationToken);
            }
            """);

        fixedSources[0].ShouldContain("First<TMessage, TResponse> : IPipelineBehavior<TMessage, TResponse> where TMessage : class\n");
        fixedSources[0].ShouldContain("Second<TMessage, TResponse> : IPipelineBehavior<TMessage, TResponse> where TMessage : notnull\n");
        ShouldBuildWithTheGenerator(fixedSources);
    }

    [Fact]
    public async Task A_stream_behaviour_calls_next_with_no_arguments()
    {
        string[] fixedSources = await Migrate(Header + """
            public sealed class Tap<TMessage, TResponse> : IStreamPipelineBehavior<TMessage, TResponse> where TMessage : IMessage
            {
                public async IAsyncEnumerable<TResponse> Handle(TMessage message, StreamHandlerDelegate<TMessage, TResponse> next, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
                {
                    await foreach (TResponse item in next(message, cancellationToken))
                    {
                        yield return item;
                    }
                }
            }
            """);

        fixedSources[0].ShouldContain("StreamHandlerDelegate<TResponse> next");
        fixedSources[0].ShouldContain("await foreach (TResponse item in next())");
        ShouldBuildWithTheGenerator(fixedSources);
    }

    [Fact]
    public async Task A_behaviour_that_passes_a_different_message_on_keeps_that_call_and_the_build_points_at_it()
    {
        string[] fixedSources = await Migrate(Header + """
            public sealed class Swap<TMessage, TResponse> : IPipelineBehavior<TMessage, TResponse> where TMessage : IMessage
            {
                public ValueTask<TResponse> Handle(TMessage message, MessageHandlerDelegate<TMessage, TResponse> next, CancellationToken cancellationToken)
                {
                    TMessage other = message;
                    return next(other, cancellationToken);
                }
            }
            """);

        // The type is still moved, as a whole; only the call that Relevox's pipeline cannot express stays.
        fixedSources[0].ShouldContain("public Task<TResponse> Handle(TMessage message, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)");
        fixedSources[0].ShouldContain("return next(other, cancellationToken);");
        BuildErrors(fixedSources).Select(static error => error.Id).ShouldContain("CS1593");
    }

    [Fact]
    public async Task Qualified_and_global_qualified_spellings_move_like_unqualified_ones()
    {
        string[] fixedSources = await Migrate("""
            using System.Threading;
            using System.Threading.Tasks;

            namespace Shop;

            public sealed record Place(int V) : Mediator.ICommand<int>;
            public sealed record Ring : global::Mediator.ICommand;

            public sealed class PlaceHandler : Mediator.ICommandHandler<Place, int>
            {
                public System.Threading.Tasks.ValueTask<int> Handle(Place command, CancellationToken cancellationToken) => new(command.V);
            }

            public sealed class RingHandler : global::Mediator.ICommandHandler<Ring>
            {
                public async ValueTask<Mediator.Unit> Handle(Ring command, CancellationToken cancellationToken)
                {
                    await Task.Yield();
                    return Mediator.Unit.Value;
                }
            }

            public sealed class Logging<TMessage, TResponse> : Mediator.IPipelineBehavior<TMessage, TResponse> where TMessage : Mediator.IMessage
            {
                public ValueTask<TResponse> Handle(TMessage message, Mediator.MessageHandlerDelegate<TMessage, TResponse> next, CancellationToken cancellationToken) => next(message, cancellationToken);
            }
            """);

        string text = fixedSources[0];
        text.ShouldContain("record Place(int V) : Relevox.IRequest<int>;");
        text.ShouldContain("record Ring : global::Relevox.IRequest<global::Relevox.Unit>;");
        text.ShouldContain("PlaceHandler : Relevox.IRequestHandler<Place, int>");
        text.ShouldContain("public System.Threading.Tasks.Task<int> Handle(Place command, CancellationToken cancellationToken) => Task.FromResult(command.V);");
        text.ShouldContain("RingHandler : global::Relevox.IRequestHandler<Ring, global::Relevox.Unit>");
        text.ShouldContain("public async Task<Relevox.Unit> Handle(Ring command");
        text.ShouldContain("return Relevox.Unit.Value;");
        text.ShouldContain("Logging<TMessage, TResponse> : Relevox.IPipelineBehavior<TMessage, TResponse> where TMessage : notnull");
        text.ShouldContain("Relevox.RequestHandlerDelegate<TResponse> next");
        text.ShouldNotContain("Mediator");
        ShouldBuildWithTheGenerator(fixedSources);
    }

    [Fact]
    public async Task An_interface_that_extends_a_command_is_moved_too()
    {
        string[] fixedSources = await Migrate(Header + """
            public interface ICreateOrder : ICommand<int>;
            public sealed record Create(int Id) : ICreateOrder;
            public sealed class CreateHandler : ICommandHandler<Create, int>
            {
                public ValueTask<int> Handle(Create command, CancellationToken cancellationToken) => new(command.Id);
            }
            """);

        fixedSources[0].ShouldContain("public interface ICreateOrder : IRequest<int>;");
        ShouldBuildWithTheGenerator(fixedSources);
    }

    [Fact]
    public async Task A_bare_AddMediator_becomes_AddRelevox_with_the_Singleton_lifetime_written_out()
    {
        string[] fixedSources = await Migrate(Header + """
            public static class Startup
            {
                public static IServiceCollection Add(IServiceCollection services) =>
                    services.AddMediator();
            }
            """);

        fixedSources[0].ShouldContain("""
                    services.AddRelevox(options =>
                    {
                        options.Lifetime = ServiceLifetime.Singleton;
                        options.HandlerLifetime = ServiceLifetime.Singleton;
                    });
            """.ReplaceLineEndings("\n"));
        ShouldBuildWithTheGenerator(fixedSources);
    }

    [Fact]
    public async Task AddMediator_with_every_option_keeps_what_Relevox_can_and_drops_the_rest()
    {
        string[] fixedSources = await Migrate(Header + """
            public sealed class Logging<TMessage, TResponse> : IPipelineBehavior<TMessage, TResponse> where TMessage : IMessage
            {
                public ValueTask<TResponse> Handle(TMessage message, MessageHandlerDelegate<TMessage, TResponse> next, CancellationToken cancellationToken) => next(message, cancellationToken);
            }

            public sealed class Tap<TMessage, TResponse> : IStreamPipelineBehavior<TMessage, TResponse> where TMessage : IMessage
            {
                public IAsyncEnumerable<TResponse> Handle(TMessage message, StreamHandlerDelegate<TMessage, TResponse> next, CancellationToken cancellationToken) => next(message, cancellationToken);
            }

            public sealed record Ping(int V) : IRequest<int>;

            public static class Startup
            {
                public static IServiceCollection Add(IServiceCollection services) =>
                    services.AddMediator(o =>
                    {
                        o.Namespace = "Shop.Mediator";
                        o.GenerateTypesAsInternal = true;
                        o.ServiceLifetime = ServiceLifetime.Scoped;
                        o.Assemblies = [typeof(Startup).Assembly];
                        o.PipelineBehaviors = new[] { typeof(Logging<,>), typeof(Logging<Ping, int>) };
                        o.StreamPipelineBehaviors = [typeof(Tap<,>)];
                        o.NotificationPublisherType = typeof(TaskWhenAllPublisher);
                    });
            }
            """);

        string text = fixedSources[0];
        text.ShouldContain("services.AddRelevox(o =>");
        text.ShouldContain("o.Lifetime = ServiceLifetime.Scoped;");
        text.ShouldContain("o.HandlerLifetime = ServiceLifetime.Scoped;");
        text.ShouldContain("o.AddOpenBehavior(typeof(Logging<,>), ServiceLifetime.Scoped);");
        text.ShouldContain("o.AddBehavior(typeof(Logging<Ping, int>), ServiceLifetime.Scoped);");
        text.ShouldContain("o.AddOpenStreamBehavior(typeof(Tap<,>), ServiceLifetime.Scoped);");
        text.ShouldContain("o.NotificationPublisherType = typeof(Relevox.NotificationPublishers.TaskWhenAllPublisher);");
        text.ShouldNotContain("o.Namespace");
        text.ShouldNotContain("o.GenerateTypesAsInternal");
        text.ShouldNotContain("o.Assemblies");
        text.ShouldNotContain("Mediator");
        ShouldBuildWithTheGenerator(fixedSources);
    }

    [Theory]
    [InlineData("typeof(Mediator.ForeachAwaitPublisher)", "ForeachAwaitPublisher")]
    [InlineData("typeof(global::Mediator.TaskWhenAllPublisher)", "TaskWhenAllPublisher")]
    [InlineData("typeof(ForeachAwaitPublisher)", "ForeachAwaitPublisher")]
    public async Task The_notification_publisher_is_named_in_Relevox_however_Mediators_was_spelled(string written, string name)
    {
        string[] fixedSources = await Migrate(Header + $$"""
            public static class Startup
            {
                public static IServiceCollection Add(IServiceCollection services) =>
                    services.AddMediator(options => options.NotificationPublisherType = {{written}});
            }
            """);

        fixedSources[0].ShouldContain($"options.NotificationPublisherType = typeof(Relevox.NotificationPublishers.{name});");
        ShouldBuildWithTheGenerator(fixedSources);
    }

    [Fact]
    public async Task The_other_ways_of_giving_behaviours_and_the_static_call_are_carried_over()
    {
        string[] fixedSources = await Migrate(Header + """
            public sealed class Logging<TMessage, TResponse> : IPipelineBehavior<TMessage, TResponse> where TMessage : IMessage
            {
                public ValueTask<TResponse> Handle(TMessage message, MessageHandlerDelegate<TMessage, TResponse> next, CancellationToken cancellationToken) => next(message, cancellationToken);
            }

            public static class Startup
            {
                public static IServiceCollection Collection(IServiceCollection services) =>
                    services.AddMediator((MediatorOptions options) =>
                    {
                        options.PipelineBehaviors = [typeof(Logging<,>)];
                    });

                public static IServiceCollection Array(IServiceCollection services) =>
                    services.AddMediator(options => options.PipelineBehaviors = new System.Type[] { typeof(Logging<,>) });

                public static IServiceCollection Static(IServiceCollection services) =>
                    MediatorDependencyInjectionExtensions.AddMediator(services);

                public static IServiceCollection StaticWithOptions(IServiceCollection services) =>
                    MediatorDependencyInjectionExtensions.AddMediator(services, options => options.ServiceLifetime = ServiceLifetime.Transient);
            }
            """);

        string text = fixedSources[0];
        text.ShouldContain("services.AddRelevox(options =>");
        text.ShouldContain("options.AddOpenBehavior(typeof(Logging<,>), ServiceLifetime.Singleton);");
        text.ShouldContain("RelevoxServiceCollectionExtensions.AddRelevox(services, options =>");
        text.ShouldContain("options.HandlerLifetime = ServiceLifetime.Transient;");
        text.ShouldNotContain("Mediator");
        ShouldBuildWithTheGenerator(fixedSources);
    }

    [Theory]
    [InlineData("services.AddMediator(options)", "System.Action<MediatorOptions> options")]
    [InlineData("services.AddMediator(Configure)", "")]
    [InlineData("services.AddMediator(options => options.NotificationPublisherType = typeof(MyPublisher))", "")]
    [InlineData("services.AddMediator(options => options.Surprise = 1)", "")]
    [InlineData("services.AddMediator(options => options.PipelineBehaviors = Behaviors)", "")]
    [InlineData("services.AddMediator(options => options.PipelineBehaviors = [typeof(Logging<,>), Other])", "")]
    [InlineData("services.AddMediator(options => options.ServiceLifetime = Pick())", "")]
    [InlineData("services.AddMediator(options => { Configure(options); })", "")]
    [InlineData("services.AddMediator(options => { /* keep these, they matter */ options.ServiceLifetime = ServiceLifetime.Scoped; })", "")]
    public async Task A_configuration_that_cannot_be_carried_over_gets_no_fix(string call, string parameter)
    {
        string source = Header + $$"""
            public sealed class MyPublisher;

            public static class Startup
            {
                public static IReadOnlyList<System.Type> Behaviors => [];

                public static System.Type Other => typeof(MyPublisher);

                public static ServiceLifetime Pick() => ServiceLifetime.Scoped;

                public static void Configure(object options)
                {
                }

                public static IServiceCollection Add(IServiceCollection services{{(parameter.Length == 0 ? "" : ", " + parameter)}}) =>
                    {{call}};
            }
            """;

        string[] fixedSources = await Migrate(source);

        fixedSources[0].ShouldContain(call);
        fixedSources[0].ShouldNotContain("AddRelevox");
    }

    [Fact]
    public void Code_that_still_has_Mediator_referenced_is_not_reported()
    {
        MetadataReference mediator = GeneratorHarness.Library("Mediator", """
            namespace Mediator
            {
                public interface IMediator;
                public interface ICommand<T>;
            }
            """);

        ImmutableArray<Diagnostic> diagnostics = GeneratorHarness.Analyze(
            new MediatorUsageAnalyzer(),
            new Dictionary<string, string>(),
            [mediator],
            "using Mediator; public sealed record Place : ICommand<int>; public static class S { public static object Add(Microsoft.Extensions.DependencyInjection.IServiceCollection s) => s.AddMediator(); }");

        diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_namespace_of_the_projects_own_called_Mediator_is_left_alone()
    {
        string[] fixedSources = await Migrate(
            "namespace Mediator { public sealed class Helper; }",
            "using Mediator; public sealed class User { public Helper Value { get; } = new(); }",
            "namespace Shop { public sealed record Place(int V) : Mediator.Helper; }");

        fixedSources[1].ShouldContain("using Mediator;");
        fixedSources[2].ShouldContain("Mediator.Helper");
    }

    [Fact]
    public async Task A_name_with_Mediator_in_the_middle_is_somebody_elses_namespace()
    {
        string[] fixedSources = await Migrate("using Company.Mediator;\nusing Mediator;\n\npublic sealed record Place : IRequest<int>;\n");

        fixedSources[0].ShouldContain("using Company.Mediator;");
        fixedSources[0].ShouldContain("using Relevox;");
        fixedSources[0].ShouldNotContain("using Mediator;");
    }

    [Fact]
    public async Task A_type_of_the_projects_own_with_Mediators_names_is_left_alone()
    {
        string[] fixedSources = await Migrate(Header + """
            public interface ICommand<T>;
            public interface INotificationHandler<T> { ValueTask Handle(T notification, CancellationToken cancellationToken); }

            public sealed record Own : ICommand<int>;
            public sealed class OwnHandler : INotificationHandler<Own>
            {
                public ValueTask Handle(Own notification, CancellationToken cancellationToken) => default;
            }
            """);

        fixedSources[0].ShouldContain("record Own : ICommand<int>;");
        fixedSources[0].ShouldContain("public ValueTask Handle(Own notification");
    }

    [Fact]
    public async Task A_pre_processor_or_another_unmappable_name_is_left_whole_and_gets_no_type_diagnostic()
    {
        string[] sources =
        [
            Header + """
            public sealed class Stamp<TMessage, TResponse> : MessagePreProcessor<TMessage, TResponse> where TMessage : IMessage
            {
                protected override ValueTask Handle(TMessage message, CancellationToken cancellationToken) => default;
            }

            public sealed class Fanout : INotificationPublisher
            {
                public ValueTask Publish<TNotification>(NotificationHandlers<TNotification> handlers, TNotification notification, CancellationToken cancellationToken) where TNotification : INotification => default;
            }

            public sealed record Place : ICommand<int>;
            """,
        ];

        (await Kinds(sources)).ShouldBe(["Name", "Type"], ignoreOrder: true);

        string[] fixedSources = await Migrate(sources);

        // Only the declaration that is in the table moves; the two that name something Relevox lacks are untouched.
        fixedSources[0].ShouldContain("MessagePreProcessor<TMessage, TResponse> where TMessage : IMessage");
        fixedSources[0].ShouldContain("protected override ValueTask Handle(TMessage message, CancellationToken cancellationToken) => default;");
        fixedSources[0].ShouldContain("Fanout : INotificationPublisher");
        fixedSources[0].ShouldContain("record Place : IRequest<int>;");
    }

    [Fact]
    public async Task The_diagnostic_reports_each_kind_once_where_it_is_and_only_as_Info()
    {
        string[] sources =
        [
            Header + """
            public sealed record Place(int V) : ICommand<int>;
            public static class Startup
            {
                public static IServiceCollection Add(IServiceCollection services) => services.AddMediator();
            }
            """,
        ];

        Compilation compilation = await Compile(sources);
        ImmutableArray<Diagnostic> diagnostics = await compilation.WithAnalyzers([new MediatorUsageAnalyzer()]).GetAnalyzerDiagnosticsAsync();

        diagnostics.Select(static diagnostic => diagnostic.Id).Distinct().ShouldBe(["RVX0017"]);
        diagnostics.Select(static diagnostic => diagnostic.Severity).Distinct().ShouldBe([DiagnosticSeverity.Info]);
        diagnostics.Select(static diagnostic => diagnostic.Properties["Kind"]).ShouldBe(["Name", "Type", "Registration"], ignoreOrder: true);
        diagnostics.Select(static diagnostic => diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture)).ShouldContain("'Place' is Mediator code; the fix 'Move from Mediator to Relevox' rewrites it");
    }

    [Fact]
    public async Task Documentation_crefs_and_a_ValueTask_variable_at_a_call_site_are_not_touched()
    {
        string[] fixedSources = await Migrate(Header + """
            public sealed record Ping : IRequest<int>;

            /// <summary>Sends through <see cref="Mediator.IMediator"/>.</summary>
            public sealed class Caller(ISender sender)
            {
                public async Task<int> Run(CancellationToken cancellationToken)
                {
                    ValueTask<int> pending = sender.Send(new Ping(), cancellationToken);
                    return await pending;
                }
            }
            """);

        fixedSources[0].ShouldContain("""<see cref="Mediator.IMediator"/>""");
        fixedSources[0].ShouldContain("ValueTask<int> pending = sender.Send(new Ping(), cancellationToken);");
        fixedSources[0].ShouldContain("using Relevox;");
    }

    [Fact]
    public async Task Usings_stay_sorted_after_the_rename()
    {
        string[] fixedSources = await Migrate(Header + """
            public sealed record Place(int V) : ICommand<int>;
            """);

        string[] usings = [.. fixedSources[0].Split('\n').TakeWhile(static line => line.StartsWith("using ", StringComparison.Ordinal))];

        usings.ShouldBe(
        [
            "using System.Collections.Generic;",
            "using System.Threading;",
            "using System.Threading.Tasks;",
            "using Microsoft.Extensions.DependencyInjection;",
            "using Relevox;",
        ]);
    }

    [Fact]
    public async Task A_using_Relevox_that_was_already_there_is_not_repeated()
    {
        string[] fixedSources = await Migrate("using Mediator;\nusing Relevox;\n\npublic sealed record Place : IRequest<int>;\n");

        fixedSources[0].Split("using Relevox;").Length.ShouldBe(2);
        fixedSources[0].ShouldNotContain("Mediator");
    }

    /// <summary>
    /// Runs the fix the way the IDE's "Fix all in solution" does: once, started on RVX0017, over every RVX0017
    /// the analyzer reports, merged by the batch fixer.
    /// </summary>
    private static async Task<string[]> Migrate(params string[] sources)
    {
        using AdhocWorkspace workspace = new();
        Project project = workspace.AddProject("Shop", LanguageNames.CSharp)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable))
            .WithParseOptions(new CSharpParseOptions(LanguageVersion.Preview).WithPreprocessorSymbols("DEBUG"))
            .WithMetadataReferences(GeneratorHarness.References);

        List<DocumentId> documents = [];
        for (int index = 0; index < sources.Length; index++)
        {
            Document document = project.AddDocument($"File{index}.cs", sources[index]);
            documents.Add(document.Id);
            project = document.Project;
        }

        MigrateFromMediator fix = new();
        AnalyzerDiagnostics provider = new();
        ImmutableArray<Diagnostic> all = await AnalyzerDiagnostics.Collect(project, CancellationToken.None);
        if (!all.IsEmpty)
        {
            Document first = project.GetDocument(all[0].Location.SourceTree)!;
            FixAllContext context = new(first, fix, FixAllScope.Solution, "Move from Mediator to Relevox", ["RVX0017"], provider, CancellationToken.None);
            CodeAction action = (await fix.GetFixAllProvider().GetFixAsync(context))!;
            ImmutableArray<CodeActionOperation> operations = await action.GetOperationsAsync(CancellationToken.None);
            project = operations.OfType<ApplyChangesOperation>().Single().ChangedSolution.GetProject(project.Id)!;
        }

        return [.. await Task.WhenAll(documents.Select(async id => (await project.GetDocument(id)!.GetTextAsync()).ToString()))];
    }

    private static async Task<Compilation> Compile(string[] sources)
    {
        using AdhocWorkspace workspace = new();
        Project project = workspace.AddProject("Shop", LanguageNames.CSharp)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable))
            .WithParseOptions(new CSharpParseOptions(LanguageVersion.Preview))
            .WithMetadataReferences(GeneratorHarness.References);
        for (int index = 0; index < sources.Length; index++)
        {
            project = project.AddDocument($"File{index}.cs", sources[index]).Project;
        }

        return (await project.GetCompilationAsync())!;
    }

    private static async Task<string[]> Kinds(string[] sources)
    {
        Compilation compilation = await Compile(sources);
        ImmutableArray<Diagnostic> diagnostics = await compilation.WithAnalyzers([new MediatorUsageAnalyzer()]).GetAnalyzerDiagnosticsAsync();
        return [.. diagnostics.Select(static diagnostic => diagnostic.Properties["Kind"]!).Distinct()];
    }

    /// <summary>What the IDE would hand a fix-all: RVX0017 from the analyzer, per document or for the project.</summary>
    private sealed class AnalyzerDiagnostics : FixAllContext.DiagnosticProvider
    {
        public override async Task<IEnumerable<Diagnostic>> GetAllDiagnosticsAsync(Project project, CancellationToken cancellationToken) =>
            await Collect(project, cancellationToken).ConfigureAwait(false);

        public override async Task<IEnumerable<Diagnostic>> GetDocumentDiagnosticsAsync(Document document, CancellationToken cancellationToken) =>
            (await Collect(document.Project, cancellationToken).ConfigureAwait(false)).Where(diagnostic => diagnostic.Location.SourceTree == document.GetSyntaxTreeAsync(cancellationToken).Result);

        public override Task<IEnumerable<Diagnostic>> GetProjectDiagnosticsAsync(Project project, CancellationToken cancellationToken) =>
            Task.FromResult(Enumerable.Empty<Diagnostic>());

        public static async Task<ImmutableArray<Diagnostic>> Collect(Project project, CancellationToken cancellationToken)
        {
            Compilation compilation = (await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false))!;
            return await compilation.WithAnalyzers([new MediatorUsageAnalyzer()]).GetAnalyzerDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static ImmutableArray<Diagnostic> BuildErrors(string[] sources)
    {
        CSharpCompilation compilation = CSharpCompilation.Create(
            "Shop",
            sources.Select((source, index) => CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview), $"File{index}.cs")),
            GeneratorHarness.References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        CSharpGeneratorDriver
            .Create([new RelevoxGenerator().AsSourceGenerator()], parseOptions: new CSharpParseOptions(LanguageVersion.Preview))
            .RunGeneratorsAndUpdateCompilation(compilation, out Compilation output, out _);

        return [.. output.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)];
    }

    private static void ShouldBuildWithTheGenerator(string[] sources) =>
        BuildErrors(sources).Select(static error => error.ToString()).ShouldBeEmpty();
}
