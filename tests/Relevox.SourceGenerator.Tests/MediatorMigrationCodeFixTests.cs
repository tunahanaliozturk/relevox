using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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
    // A cref the compiler cannot resolve, finds ambiguous, or whose parameters or syntax it cannot read.
    private static readonly string[] UnresolvedCref = ["CS0419", "CS1574", "CS1580", "CS1581", "CS1584"];

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
    public async Task A_returned_ValueTask_of_the_projects_own_gets_AsTask()
    {
        // The second handler is the shape found in the Blazor template: three Handle methods for three events, each
        // returning a call to one private helper that returns ValueTask.
        string[] fixedSources = await Migrate(Header + """
            public sealed record Ping(int V) : IRequest<int>;
            public sealed class PingHandler : IRequestHandler<Ping, int>
            {
                public ValueTask<int> Handle(Ping request, CancellationToken cancellationToken) => Compute(request.V);

                private static ValueTask<int> Compute(int value) => new(value);
            }

            public sealed record Created : INotification;
            public sealed record Deleted : INotification;
            public sealed class ChangedHandler : INotificationHandler<Created>, INotificationHandler<Deleted>
            {
                public ValueTask Handle(Created notification, CancellationToken cancellationToken) => Refresh(cancellationToken);

                public ValueTask Handle(Deleted notification, CancellationToken cancellationToken)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return ValueTask.CompletedTask;
                    }

                    return true ? Refresh(cancellationToken) : default;
                }

                private static async ValueTask Refresh(CancellationToken cancellationToken) => await Task.Delay(1, cancellationToken);
            }
            """);

        string text = fixedSources[0];
        text.ShouldContain("public Task<int> Handle(Ping request, CancellationToken cancellationToken) => Compute(request.V).AsTask();");
        text.ShouldContain("public Task Handle(Created notification, CancellationToken cancellationToken) => Refresh(cancellationToken).AsTask();");
        text.ShouldContain("return Task.CompletedTask;");
        text.ShouldContain("return (true ? Refresh(cancellationToken) : default).AsTask();");
        text.ShouldContain("private static async ValueTask Refresh(");
        ShouldBuildWithTheGenerator(fixedSources);
    }

    [Fact]
    public async Task A_ValueTask_built_around_a_task_returns_the_task()
    {
        string[] fixedSources = await Migrate(Header + """
            public sealed record Ping(int V) : IRequest<int>;
            public sealed class PingHandler : IRequestHandler<Ping, int>
            {
                public ValueTask<int> Handle(Ping request, CancellationToken cancellationToken) => new ValueTask<int>(Load(request.V));

                private static Task<int> Load(int value) => Task.FromResult(value);
            }

            public sealed record Pong(int V) : IRequest<int>;
            public sealed class PongHandler : IRequestHandler<Pong, int>
            {
                public ValueTask<int> Handle(Pong request, CancellationToken cancellationToken)
                {
                    return new(Task.Run(() => request.V, cancellationToken));
                }
            }
            """);

        fixedSources[0].ShouldContain("public Task<int> Handle(Ping request, CancellationToken cancellationToken) => Load(request.V);");
        fixedSources[0].ShouldContain("return Task.Run(() => request.V, cancellationToken);");
        ShouldBuildWithTheGenerator(fixedSources);
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
    public async Task A_file_marked_auto_generated_is_reported_like_any_other()
    {
        // Scaffolding tools write this header on code that is then checked in, as the Blazor template in
        // docs/migration-from-mediator.md does on whole feature folders. Analyzers skip such files by default, which
        // left them out of the move. Fix all still skips them, which is Roslyn's rule and not the analyzer's, so the
        // guide says to mark them generated_code = false; the diagnostic and its single fix are there either way.
        string[] sources =
        [
            """
            // <auto-generated>
            // Written by a scaffolding tool.
            // </auto-generated>

            """ + Header + """
            public sealed record Ping(int V) : IRequest<int>;
            public sealed class PingHandler : IRequestHandler<Ping, int>
            {
                public ValueTask<int> Handle(Ping request, CancellationToken cancellationToken) => new(request.V);
            }
            """,
        ];

        (await Kinds(sources)).ShouldBe(["Name", "Type"], ignoreOrder: true);
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
    public async Task AsTask_on_next_is_dropped_since_Relevox_next_returns_a_Task_already()
    {
        // The shape of a caching behaviour handing next to a cache that wants a Task, found in the Blazor template.
        string[] fixedSources = await Migrate(Header + """
            public sealed class Caching<TMessage, TResponse> : IPipelineBehavior<TMessage, TResponse> where TMessage : IMessage
            {
                public async ValueTask<TResponse> Handle(TMessage message, MessageHandlerDelegate<TMessage, TResponse> next, CancellationToken cancellationToken) =>
                    await GetOrSet(_ => next(message, cancellationToken).AsTask(), cancellationToken);

                private static Task<TResponse> GetOrSet(System.Func<CancellationToken, Task<TResponse>> factory, CancellationToken cancellationToken) =>
                    factory(cancellationToken);
            }
            """);

        fixedSources[0].ShouldContain("await GetOrSet(_ => next(cancellationToken), cancellationToken);");
        ShouldBuildWithTheGenerator(fixedSources);
    }

    [Fact]
    public async Task A_local_function_parameter_that_shadows_next_is_not_rewritten()
    {
        string[] fixedSources = await Migrate(Header + """
            public sealed class Retry<TMessage, TResponse> : IPipelineBehavior<TMessage, TResponse> where TMessage : IMessage
            {
                public async ValueTask<TResponse> Handle(TMessage message, MessageHandlerDelegate<TMessage, TResponse> next, CancellationToken cancellationToken)
                {
                    int tries = Twice(static (message, cancellationToken) => 1, message, cancellationToken);
                    return await next(message, cancellationToken).AsTask();

                    static int Twice(System.Func<TMessage, CancellationToken, int> next, TMessage message, CancellationToken cancellationToken) =>
                        next(message, cancellationToken) + next(message, cancellationToken);
                }
            }
            """);

        fixedSources[0].ShouldContain("return await next(cancellationToken);");
        fixedSources[0].ShouldContain("next(message, cancellationToken) + next(message, cancellationToken);");
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

    [Fact]
    public async Task A_publisher_of_the_projects_own_is_kept_and_exception_handlers_leave_the_behaviours()
    {
        // The registration of the Blazor template in docs/migration-from-mediator.md, cut down: a publisher of its
        // own, a scoped lifetime, and exception handlers listed with the behaviours. The publisher and the exception
        // handler, in the second file, are left for the compiler; the registration in the first builds.
        string[] fixedSources = await Migrate(
            Header + """
            public sealed class Logging<TMessage, TResponse> : IPipelineBehavior<TMessage, TResponse> where TMessage : IMessage
            {
                public ValueTask<TResponse> Handle(TMessage message, MessageHandlerDelegate<TMessage, TResponse> next, CancellationToken cancellationToken) => next(message, cancellationToken);
            }

            public static class Startup
            {
                public static IServiceCollection Add(IServiceCollection services) =>
                    services.AddMediator(options =>
                    {
                        options.NotificationPublisherType = typeof(NoWaitPublisher);
                        options.ServiceLifetime = ServiceLifetime.Scoped;
                        options.PipelineBehaviors = [typeof(Fallback<,>), typeof(Logging<,>)];
                    });
            }
            """,
            Header + """
            public sealed class Fallback<TMessage, TResponse> : MessageExceptionHandler<TMessage, TResponse> where TMessage : notnull, IMessage
            {
                protected override ValueTask<ExceptionHandlingResult<TResponse>> Handle(TMessage message, System.Exception exception, CancellationToken cancellationToken) => NotHandled;
            }

            public sealed class NoWaitPublisher : INotificationPublisher
            {
                public ValueTask Publish<TNotification>(NotificationHandlers<TNotification> handlers, TNotification notification, CancellationToken cancellationToken)
                    where TNotification : INotification => default;
            }
            """);

        string text = fixedSources[0];
        text.ShouldContain("options.NotificationPublisherType = typeof(NoWaitPublisher);");
        text.ShouldContain("options.AddOpenBehavior(typeof(Logging<,>), ServiceLifetime.Scoped);");
        text.ShouldNotContain("Fallback");
        ImmutableArray<Diagnostic> errors = BuildErrors(fixedSources);
        errors.ShouldNotBeEmpty();
        errors.Select(static error => error.Location.SourceTree?.FilePath).Distinct().ShouldBe(["File1.cs"]);
    }

    [Fact]
    public async Task A_publisher_of_the_projects_own_named_like_Mediators_is_kept()
    {
        string[] fixedSources = await Migrate(Header + """
            public sealed class TaskWhenAllPublisher;

            public static class Startup
            {
                public static IServiceCollection Add(IServiceCollection services) =>
                    services.AddMediator(options => options.NotificationPublisherType = typeof(TaskWhenAllPublisher));
            }
            """);

        fixedSources[0].ShouldContain("options.NotificationPublisherType = typeof(TaskWhenAllPublisher);");
        fixedSources[0].ShouldNotContain("Relevox.NotificationPublishers");
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
    [InlineData("services.AddMediator(options => options.NotificationPublisherType = Other)", "")]
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
            """
            using Mediator;

            /// <summary>An <see cref="ICommand{T}"/>, not yet a <see cref="Mediator.IQuery{T}"/>.</summary>
            public sealed record Place : ICommand<int>;

            public static class S { public static object Add(Microsoft.Extensions.DependencyInjection.IServiceCollection s) => s.AddMediator(); }
            """);

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
    public async Task A_ValueTask_variable_at_a_call_site_is_not_touched()
    {
        string[] fixedSources = await Migrate(Header + """
            public sealed record Ping : IRequest<int>;

            public sealed class Caller(ISender sender)
            {
                public async Task<int> Run(CancellationToken cancellationToken)
                {
                    ValueTask<int> pending = sender.Send(new Ping(), cancellationToken);
                    return await pending;
                }
            }
            """);

        fixedSources[0].ShouldContain("ValueTask<int> pending = sender.Send(new Ping(), cancellationToken);");
        fixedSources[0].ShouldContain("using Relevox;");
    }

    [Fact]
    public async Task Mediator_names_in_documentation_crefs_are_moved_by_the_same_fix_all_as_the_code()
    {
        // With documentation generation on and warnings as errors, a cref that no longer resolves breaks the build.
        // Sender is not in Mediator's shape, so its crefs are fixed one by one; Place and PlaceHandler are, so theirs
        // move with the rewrite of the type, the nested Notes included.
        string[] sources =
        [
            Header + """
            /// <summary>
            /// Sends through <see cref="Mediator.ISender.Send{TResponse}(Mediator.ICommand{TResponse}, System.Threading.CancellationToken)"/>,
            /// so the request is a <see cref="Mediator.IQuery{TResponse}"/> or a <see cref="global::Mediator.ICommand"/>,
            /// handled by an <see cref="ICommandHandler{TCommand}"/> and sent by
            /// <see cref="ISender.Send{TResponse}(IQuery{TResponse}, CancellationToken)"/>.
            /// <see cref="IMediator"/> resolves again once the using moves.
            /// </summary>
            /// <seealso cref="Mediator.IPublisher"/>
            public static class Sender
            {
                /// <returns><see cref="Mediator.Unit.Value"/>.</returns>
                /// <exception cref="Mediator.IMediator">Never.</exception>
                public static Mediator.Unit Done() => Mediator.Unit.Value;

                /// <inheritdoc cref="Mediator.ISender.Send{TResponse}(Mediator.IQuery{TResponse}, System.Threading.CancellationToken)"/>
                public static void Inherited() { }
            }

            /// <summary>Placed through an <see cref="ICommandHandler{TCommand, TResponse}"/>.</summary>
            public sealed record Place(int Value) : ICommand<int>;

            /// <summary>Handles a <see cref="Place"/> as an <see cref="ICommandHandler{TCommand, TResponse}"/>.</summary>
            public sealed class PlaceHandler : ICommandHandler<Place, int>
            {
                /// <inheritdoc cref="ICommandHandler{TCommand, TResponse}.Handle(TCommand, CancellationToken)"/>
                public ValueTask<int> Handle(Place command, CancellationToken cancellationToken) => new(command.Value);

                /// <summary>Not a handler, inside one: <see cref="Mediator.IStreamQuery{TResponse}"/>.</summary>
                public static class Notes;
            }
            """,
        ];

        (await Kinds(sources)).ShouldContain("Cref");

        string[] fixedSources = await Migrate(sources);

        string text = fixedSources[0];
        text.ShouldNotContain("Mediator.");
        text.ShouldNotContain("using Mediator;");
        text.ShouldContain("""<see cref="Relevox.ISender.Send{TResponse}(Relevox.IRequest{TResponse}, System.Threading.CancellationToken)"/>""");
        text.ShouldContain("""<see cref="Relevox.IRequest{TResponse}"/>""");
        text.ShouldContain("""<see cref="global::Relevox.IRequest{Unit}"/>""");
        text.ShouldContain("""<see cref="IRequestHandler{TCommand, Unit}"/>""");
        text.ShouldContain("""<see cref="ISender.Send{TResponse}(IRequest{TResponse}, CancellationToken)"/>""");
        text.ShouldContain("""<see cref="IMediator"/>""");
        text.ShouldContain("""<seealso cref="Relevox.IPublisher"/>""");
        text.ShouldContain("""<returns><see cref="Relevox.Unit.Value"/>.</returns>""");
        text.ShouldContain("""<exception cref="Relevox.IMediator">""");
        text.ShouldContain("""<inheritdoc cref="Relevox.ISender.Send{TResponse}(Relevox.IRequest{TResponse}, System.Threading.CancellationToken)"/>""");
        text.ShouldContain("""<summary>Placed through an <see cref="IRequestHandler{TCommand, TResponse}"/>.</summary>""");
        text.ShouldContain("""<summary>Handles a <see cref="Place"/> as an <see cref="IRequestHandler{TCommand, TResponse}"/>.</summary>""");
        text.ShouldContain("""<inheritdoc cref="IRequestHandler{TCommand, TResponse}.Handle(TCommand, CancellationToken)"/>""");
        text.ShouldContain("""<see cref="Relevox.IStreamRequest{TResponse}"/>""");
        text.ShouldContain("public Task<int> Handle(Place command, CancellationToken cancellationToken) => Task.FromResult(command.Value);");
        ShouldBuildWithTheGenerator(fixedSources);
        NothingLeftToMove(fixedSources);
    }

    [Fact]
    public async Task A_cref_with_no_Relevox_counterpart_or_one_that_would_name_a_type_of_your_own_is_left_for_the_compiler()
    {
        // None of these would resolve into Relevox: MessagePreProcessor has no counterpart, Relevox's IMediator has no
        // Missing, MessageHandlerDelegate is not in the table, Shop's own IStreamRequest<T> would be what
        // IStreamRequest{TResponse} names here, and the second file has no using of Mediator to become one of
        // Relevox. They are not reported, and the compiler's CS1574 points at each.
        string[] sources =
        [
            Header + """
            public interface IStreamRequest<T>;

            /// <summary>
            /// Not <see cref="Mediator.MessagePreProcessor{TMessage, TResponse}"/>, nor <see cref="Mediator.IMediator.Missing"/>,
            /// nor <see cref="MessageHandlerDelegate{TMessage, TResponse}"/>, nor <see cref="IStreamQuery{TResponse}"/>,
            /// nor <see cref="Mediator.ISender.Send{TResponse}(Mediator.IQuery{TResponse}, int)"/>, whose parameters are wrong.
            /// </summary>
            public static class Sender;
            """,
            """
            /// <summary>Not <see cref="ICommand{TResponse}"/> either.</summary>
            public static class Elsewhere;
            """,
        ];

        (await Kinds(sources)).ShouldBe(["Name"]);

        string[] fixedSources = await Migrate(sources);

        fixedSources[0].ShouldContain("using Relevox;");
        fixedSources[0].ShouldContain("""<see cref="Mediator.MessagePreProcessor{TMessage, TResponse}"/>""");
        fixedSources[0].ShouldContain("""<see cref="Mediator.IMediator.Missing"/>""");
        fixedSources[0].ShouldContain("""<see cref="MessageHandlerDelegate{TMessage, TResponse}"/>""");
        fixedSources[0].ShouldContain("""<see cref="IStreamQuery{TResponse}"/>""");
        fixedSources[0].ShouldContain("""<see cref="Mediator.ISender.Send{TResponse}(Mediator.IQuery{TResponse}, int)"/>""");
        fixedSources[1].ShouldContain("""<see cref="ICommand{TResponse}"/>""");
        BuildErrors(fixedSources).Select(static diagnostic => diagnostic.Id).ShouldBe(["CS1574", "CS1574", "CS1574", "CS1574", "CS1574", "CS1574"]);
        NothingLeftToMove(fixedSources);
    }

    public static TheoryData<string, string> CrefRows => new()
    {
        { "IRequest{T}", "IRequest{T}" },
        { "ICommand{T}", "IRequest{T}" },
        { "IQuery{T}", "IRequest{T}" },
        { "IRequest", "IRequest{Unit}" },
        { "ICommand", "IRequest{Unit}" },
        { "IRequestHandler{TRequest, T}", "IRequestHandler{TRequest, T}" },
        { "ICommandHandler{TRequest, T}", "IRequestHandler{TRequest, T}" },
        { "IQueryHandler{TRequest, T}", "IRequestHandler{TRequest, T}" },
        { "IRequestHandler{TRequest}", "IRequestHandler{TRequest, Unit}" },
        { "ICommandHandler{TRequest}", "IRequestHandler{TRequest, Unit}" },
        { "IStreamRequest{T}", "IStreamRequest{T}" },
        { "IStreamCommand{T}", "IStreamRequest{T}" },
        { "IStreamQuery{T}", "IStreamRequest{T}" },
        { "IStreamRequestHandler{TRequest, T}", "IStreamRequestHandler{TRequest, T}" },
        { "IStreamCommandHandler{TRequest, T}", "IStreamRequestHandler{TRequest, T}" },
        { "IStreamQueryHandler{TRequest, T}", "IStreamRequestHandler{TRequest, T}" },
        { "INotification", "INotification" },
        { "INotificationHandler{T}", "INotificationHandler{T}" },
        { "IPipelineBehavior{TMessage, T}", "IPipelineBehavior{TMessage, T}" },
        { "IStreamPipelineBehavior{TMessage, T}", "IStreamPipelineBehavior{TMessage, T}" },
    };

    [Theory]
    [MemberData(nameof(CrefRows))]
    public async Task Each_row_of_the_mapping_table_moves_as_a_cref_qualified_or_short(string mediator, string relevox)
    {
        string[] fixedSources = await Migrate(Header + $$"""
            /// <summary>A <see cref="Mediator.{{mediator}}"/>, or a <see cref="{{mediator}}"/>.</summary>
            public static class Sender;
            """);

        fixedSources[0].ShouldContain($"""<see cref="Relevox.{relevox}"/>""");
        fixedSources[0].ShouldContain($"""<see cref="{relevox}"/>""");
        ShouldBuildWithTheGenerator(fixedSources);
        NothingLeftToMove(fixedSources);
    }

    [Fact]
    public async Task A_short_cref_moves_under_a_global_using_a_using_in_its_namespace_or_one_of_Relevox()
    {
        string[] fixedSources = await Migrate(
            "global using Mediator;\n",
            """
            namespace Shop.Global;

            /// <summary>A <see cref="ICommand{TResponse}"/>.</summary>
            public static class Sender;
            """,
            """
            namespace Shop.Inner
            {
                using Mediator;

                /// <summary>A <see cref="IQuery{TResponse}"/>.</summary>
                public static class Sender;
            }
            """,
            """
            using Relevox;

            namespace Shop.Moved;

            /// <summary>A <see cref="ICommandHandler{TCommand}"/> or a <see cref="Mediator.IStreamQuery{TResponse}"/>.</summary>
            public static class Sender;
            """);

        fixedSources[0].ShouldContain("global using Relevox;");
        fixedSources[1].ShouldContain("""<see cref="IRequest{TResponse}"/>""");
        fixedSources[2].ShouldContain("using Relevox;");
        fixedSources[2].ShouldContain("""<see cref="IRequest{TResponse}"/>""");
        fixedSources[3].ShouldContain("""<see cref="IRequestHandler{TCommand, Unit}"/> or a <see cref="Relevox.IStreamRequest{TResponse}"/>""");
        ShouldBuildWithTheGenerator(fixedSources);
        NothingLeftToMove(fixedSources);
    }

    [Fact]
    public async Task A_cref_through_an_alias_of_Mediator_keeps_the_alias_and_moves_the_name()
    {
        string[] fixedSources = await Migrate("""
            using M = Mediator;

            namespace Shop;

            /// <summary>A <see cref="M.ICommand{TResponse}"/>, through <see cref="M.IMediator"/>.</summary>
            public static class Sender;
            """);

        fixedSources[0].ShouldContain("using M = Relevox;");
        fixedSources[0].ShouldContain("""<see cref="M.IRequest{TResponse}"/>, through <see cref="M.IMediator"/>""");
        ShouldBuildWithTheGenerator(fixedSources);
        NothingLeftToMove(fixedSources);
    }

    [Fact]
    public async Task A_cref_that_does_not_parse_is_neither_reported_nor_moved()
    {
        string[] sources =
        [
            Header + """
            /// <summary>A <see cref="Mediator.ICommand{T} garbage"/>.</summary>
            public static class Sender;
            """,
        ];

        (await Kinds(sources)).ShouldBe(["Name"]);
        (await Migrate(sources))[0].ShouldContain("""<see cref="Mediator.ICommand{T} garbage"/>""");
    }

    [Fact]
    public async Task A_cref_in_a_misplaced_comment_inside_a_moved_type_moves_with_it()
    {
        // The compiler warns that the comment is misplaced (CS1587), but its cref is reported, so the rewrite of the
        // type has to move it.
        string[] fixedSources = await Migrate(Header + """
            public sealed record Place(int Value) :
                /// <see cref="Mediator.IQuery{TResponse}"/>
                ICommand<int>;
            """);

        fixedSources[0].ShouldContain("""/// <see cref="Relevox.IRequest{TResponse}"/>""");
        fixedSources[0].ShouldContain("IRequest<int>;");
        NothingLeftToMove(fixedSources);
    }

    [Fact]
    public async Task A_cref_on_a_delegate_beside_a_moved_type_is_moved_in_the_same_fix_all()
    {
        // The delegate is in no type, so its cref takes the cref's own fix, next to the rewrite of the record.
        string[] fixedSources = await Migrate(Header + """
            /// <summary>Called with a <see cref="Mediator.ICommand{TResponse}"/>.</summary>
            public delegate void Placed(Place place);

            /// <summary>An <see cref="ICommand{TResponse}"/>.</summary>
            public sealed record Place(int Value) : ICommand<int>;
            """);

        fixedSources[0].ShouldContain("""<summary>Called with a <see cref="Relevox.IRequest{TResponse}"/>.</summary>""");
        fixedSources[0].ShouldContain("""<summary>An <see cref="IRequest{TResponse}"/>.</summary>""");
        fixedSources[0].ShouldContain("record Place(int Value) : IRequest<int>;");
        ShouldBuildWithTheGenerator(fixedSources);
        NothingLeftToMove(fixedSources);
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

    /// <summary>
    /// The errors of a build with the generator, and the warnings for a cref that does not resolve: documentation
    /// comments are checked, as with GenerateDocumentationFile on, where warnings as errors makes those break the build.
    /// </summary>
    private static ImmutableArray<Diagnostic> BuildErrors(string[] sources)
    {
        CSharpParseOptions parse = new CSharpParseOptions(LanguageVersion.Preview).WithDocumentationMode(DocumentationMode.Diagnose);
        CSharpCompilation compilation = CSharpCompilation.Create(
            "Shop",
            sources.Select((source, index) => CSharpSyntaxTree.ParseText(source, parse, $"File{index}.cs")),
            GeneratorHarness.References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        CSharpGeneratorDriver
            .Create([new RelevoxGenerator().AsSourceGenerator()], parseOptions: parse)
            .RunGeneratorsAndUpdateCompilation(compilation, out Compilation output, out _);

        return [.. output.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error || UnresolvedCref.Contains(diagnostic.Id))];
    }

    /// <summary>
    /// No cref RVX0017 would move is left after one "Fix all": each one was either reported and fixed on its own, or
    /// moved by the rewrite of the type it is in.
    /// </summary>
    private static void NothingLeftToMove(string[] sources)
    {
        CSharpCompilation compilation = CSharpCompilation.Create(
            "Shop",
            sources.Select((source, index) => CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview), $"File{index}.cs")),
            GeneratorHarness.References);
        foreach (SyntaxTree tree in compilation.SyntaxTrees)
        {
            SemanticModel model = compilation.GetSemanticModel(tree);
            tree.GetRoot().DescendantNodes(descendIntoTrivia: true).OfType<XmlCrefAttributeSyntax>()
                .Where(attribute => MediatorSyntax.MovedCref(attribute.Cref, model, CancellationToken.None) is not null)
                .Select(static attribute => attribute.ToString())
                .ShouldBeEmpty();
        }
    }

    private static void ShouldBuildWithTheGenerator(string[] sources) =>
        BuildErrors(sources).Select(static error => error.ToString()).ShouldBeEmpty();
}
