namespace Relevox.SourceGenerator.Tests;

/// <summary>
/// <c>Send(object)</c> in a project with no built-in behaviour, where nothing else makes the send asynchronous:
/// a throw before the handler's first await has to end up in the task, as MediatR's asynchronous wrapper puts it,
/// faulted for an exception and canceled for <see cref="OperationCanceledException"/>, and what the handler sets in
/// the execution context must not reach the caller.
/// </summary>
public sealed class ObjectSendTests
{
    private const string Program = """
        using Microsoft.Extensions.DependencyInjection;
        using Relevox;
        using System;
        using System.Threading;
        using System.Threading.Tasks;

        public sealed record Throws : IRequest<int>;
        public sealed class ThrowsHandler : IRequestHandler<Throws, int>
        {
            public Task<int> Handle(Throws request, CancellationToken cancellationToken) => throw new NotSupportedException();
        }

        public sealed record Cancels : IRequest<int>;
        public sealed class CancelsHandler : IRequestHandler<Cancels, int>
        {
            public Task<int> Handle(Cancels request, CancellationToken cancellationToken) => throw new OperationCanceledException();
        }

        public sealed record ThrowsQuietly : IRequest;
        public sealed class ThrowsQuietlyHandler : IRequestHandler<ThrowsQuietly>
        {
            public Task Handle(ThrowsQuietly request, CancellationToken cancellationToken) => throw new NotSupportedException();
        }

        public sealed record CancelsQuietly : IRequest;
        public sealed class CancelsQuietlyHandler : IRequestHandler<CancelsQuietly>
        {
            public Task Handle(CancelsQuietly request, CancellationToken cancellationToken) => throw new OperationCanceledException();
        }

        public static class Ambient
        {
            public static readonly AsyncLocal<string?> Local = new();
        }

        public sealed record Answers : IRequest<int>;
        public sealed class AnswersHandler : IRequestHandler<Answers, int>
        {
            public Task<int> Handle(Answers request, CancellationToken cancellationToken)
            {
                Ambient.Local.Value = "handler";
                return Task.FromResult(42);
            }
        }

        public sealed record Done : IRequest;
        public sealed class DoneHandler : IRequestHandler<Done>
        {
            public Task Handle(Done request, CancellationToken cancellationToken)
            {
                Ambient.Local.Value = "handler";
                return Task.CompletedTask;
            }
        }

        public sealed record Yields : IRequest<int>;
        public sealed class YieldsHandler : IRequestHandler<Yields, int>
        {
            public async Task<int> Handle(Yields request, CancellationToken cancellationToken)
            {
                await Task.Yield();
                return 7;
            }
        }

        public static class Program
        {
            public static async Task<string> Run()
            {
                await using ServiceProvider services = new ServiceCollection().AddRelevox().BuildServiceProvider();
                ISender sender = services.GetRequiredService<ISender>();
                string[] outcomes =
                [
                    await Outcome(() => sender.Send((object)new Throws())),
                    await Outcome(() => sender.Send((object)new Cancels())),
                    await Outcome(() => sender.Send((object)new ThrowsQuietly())),
                    await Outcome(() => sender.Send((object)new CancelsQuietly())),
                    await Outcome(() => sender.Send((object)new Answers())),
                    await Outcome(() => sender.Send((object)new Done())),
                    await Outcome(() => sender.Send((object)new Yields())),
                ];
                return string.Join(" | ", outcomes);
            }

            private static async Task<string> Outcome(Func<Task<object?>> send)
            {
                Task<object?> task;
                try
                {
                    task = send();
                }
                catch (Exception exception)
                {
                    return "threw " + exception.GetType().Name;
                }

                // A handler that finished synchronously ran on this thread; what it set must not be seen here.
                if (Ambient.Local.Value is not null)
                {
                    return "saw " + Ambient.Local.Value;
                }

                try
                {
                    return "returned " + (await task)?.GetType().Name;
                }
                catch (Exception exception)
                {
                    return task.Status + " " + exception.GetType().Name;
                }
            }
        }
        """;

    [Fact]
    public async Task A_throw_before_the_first_await_ends_up_in_the_task_and_the_handler_context_stays_its_own()
    {
        string outcomes = await GeneratorHarness.Execute(Program);

        outcomes.ShouldBe(string.Join(" | ",
            "Faulted NotSupportedException",
            "Canceled OperationCanceledException",
            "Faulted NotSupportedException",
            "Canceled OperationCanceledException",
            "returned Int32",
            "returned Unit",
            "returned Int32"));
    }
}
