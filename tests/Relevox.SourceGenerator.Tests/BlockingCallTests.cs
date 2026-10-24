using System.Globalization;
using Microsoft.CodeAnalysis;
using Relevox.Migration;

namespace Relevox.SourceGenerator.Tests;

/// <summary>RVX0010, blocking on a task inside a handler, behaviour or processor (#49).</summary>
public sealed class BlockingCallTests
{
    private const string Usings = "using Relevox; using Relevox.Pipeline; using System; using System.Linq; using System.Threading; using System.Threading.Tasks;\n";

    private const string Work = """
        public sealed record Ping : IRequest<int>;
        public sealed class PingHandler : IRequestHandler<Ping, int>
        {
            public Task<int> Handle(Ping request, CancellationToken cancellationToken) => Task.FromResult(1);
        }

        public interface IStore
        {
            Task<int> Load();

            ValueTask<int> Peek();

            Task Save();

            ValueTask Flush();
        }

        """;

    [Fact]
    public void Result_on_a_task_in_a_handler_is_reported_at_the_property()
    {
        Diagnostic diagnostic = Analyze(Work + """
            public sealed record Get : IRequest<int>;
            public sealed class GetHandler(IStore store) : IRequestHandler<Get, int>
            {
                public Task<int> Handle(Get request, CancellationToken cancellationToken) => Task.FromResult(store.Load().Result);
            }
            """).ShouldHaveSingleItem();

        diagnostic.Id.ShouldBe("RVX0010");
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Info);
        diagnostic.GetMessage(CultureInfo.InvariantCulture).ShouldBe("'GetHandler' blocks on a task with '.Result'; await it instead");
        diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan).ShouldBe("Result");
    }

    [Fact]
    public void Every_form_is_reported_for_Task_and_ValueTask()
    {
        string[] messages = [.. Analyze(Work + """
            public sealed record Get : IRequest<int>;
            public sealed class GetHandler(IStore store) : IRequestHandler<Get, int>
            {
                public Task<int> Handle(Get request, CancellationToken cancellationToken)
                {
                    store.Save().Wait();
                    store.Save().Wait(100);
                    store.Save().GetAwaiter().GetResult();
                    store.Save().ConfigureAwait(false).GetAwaiter().GetResult();
                    store.Flush().GetAwaiter().GetResult();
                    store.Flush().ConfigureAwait(false).GetAwaiter().GetResult();
                    int a = store.Load().GetAwaiter().GetResult();
                    int b = store.Peek().Result;
                    int c = store.Peek().GetAwaiter().GetResult();
                    return Task.FromResult(a + b + c);
                }
            }
            """).Select(static diagnostic => diagnostic.GetMessage(CultureInfo.InvariantCulture))];

        messages.ShouldBe(
        [
            .. Enumerable.Repeat("'GetHandler' blocks on a task with '.Wait()'; await it instead", 2),
            .. Enumerable.Repeat("'GetHandler' blocks on a task with '.GetAwaiter().GetResult()'; await it instead", 5),
            "'GetHandler' blocks on a task with '.Result'; await it instead",
            "'GetHandler' blocks on a task with '.GetAwaiter().GetResult()'; await it instead",
        ]);
    }

    [Fact]
    public void Behaviours_processors_exception_actions_and_notification_handlers_are_pipeline_code_too()
    {
        Analyze(Work + """
            public sealed class Slow<TRequest, TResponse>(IStore store) : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
            {
                public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
                {
                    store.Save().Wait();
                    return next(cancellationToken);
                }
            }

            public sealed class Before(IStore store) : IRequestPreProcessor<Ping>
            {
                public Task Process(Ping request, CancellationToken cancellationToken)
                {
                    _ = store.Load().Result;
                    return Task.CompletedTask;
                }
            }

            public sealed class OnFailure(IStore store) : IRequestExceptionAction<Ping, Exception>
            {
                public Task Execute(Ping request, Exception exception, CancellationToken cancellationToken)
                {
                    store.Save().GetAwaiter().GetResult();
                    return Task.CompletedTask;
                }
            }

            public sealed record Happened : INotification;
            public sealed class Listener(IStore store) : INotificationHandler<Happened>
            {
                public Task Handle(Happened notification, CancellationToken cancellationToken)
                {
                    store.Save().Wait();
                    return Task.CompletedTask;
                }
            }
            """).Select(static diagnostic => diagnostic.GetMessage(CultureInfo.InvariantCulture).Split(' ')[0]).ShouldBe(["'Slow'", "'Before'", "'OnFailure'", "'Listener'"]);
    }

    [Fact]
    public void A_task_that_is_already_finished_is_not_reported()
    {
        Analyze(Work + """
            public sealed record Get : IRequest<int>;
            public sealed class GetHandler(IStore store) : IRequestHandler<Get, int>
            {
                public Task<int> Handle(Get request, CancellationToken cancellationToken)
                {
                    int a = Task.FromResult(1).Result;
                    Task.CompletedTask.Wait();
                    Task.CompletedTask.GetAwaiter().GetResult();
                    int b = new ValueTask<int>(2).Result;
                    int c = ValueTask.FromResult(3).GetAwaiter().GetResult();
                    bool done = store.Save().Wait(0) && store.Save().Wait(TimeSpan.Zero) && store.Save().Wait(0, cancellationToken);
                    return store.Load().ContinueWith(task => task.Result + new[] { a, b, c }.Select(x => x + task.Result).Sum() + (done ? 1 : 0), cancellationToken);
                }
            }
            """).ShouldBeEmpty();
    }

    [Fact]
    public void Blocking_outside_the_pipeline_and_a_Result_that_is_not_a_task_are_not_reported()
    {
        Analyze(Work + """
            public sealed class Outcome { public int Result { get; } }

            public sealed class Controller(IStore store)
            {
                public int Get() => store.Load().Result;
            }

            public sealed record Get : IRequest<int>;
            public sealed class GetHandler : IRequestHandler<Get, int>
            {
                public Task<int> Handle(Get request, CancellationToken cancellationToken) => Task.FromResult(new Outcome().Result);
            }
            """).ShouldBeEmpty();
    }

    private static Diagnostic[] Analyze(string source) =>
        [.. GeneratorHarness.Analyze(new BlockingCallAnalyzer(), Usings + source).OrderBy(static diagnostic => diagnostic.Location.SourceSpan.Start)];
}
