namespace Relevox.SourceGenerator.Tests;

/// <summary>
/// The pre- and post-processor behaviours avoid a state machine when everything finishes synchronously, and must
/// still behave as the <c>async</c> methods MediatR has: a behaviour outside them sees a faulted task, or a canceled
/// one, never a synchronous throw, and none of the handler's changes to the execution context. Seen from a synchronous behaviour registered before <c>AddRelevox</c>, in a
/// project without exception handlers, whose asynchronous behaviours would otherwise hide the difference.
/// </summary>
public sealed class ProcessorBehaviorTests
{
    private const string Program = """
        using Microsoft.Extensions.DependencyInjection;
        using Relevox;
        using Relevox.Pipeline;
        using System;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;

        public enum Act { Answer, Throw, Cancel, Yield }

        public sealed record Ask(Act Before, Act Handler, Act After) : IRequest<int>;

        public sealed class AskHandler(List<string> trace) : IRequestHandler<Ask, int>
        {
            public Task<int> Handle(Ask request, CancellationToken cancellationToken)
            {
                trace.Add("handler");
                Acts.Ambient.Value = "handler";
                return Acts.Run(request.Handler, 42);
            }
        }

        public sealed class Before(List<string> trace) : IRequestPreProcessor<Ask>
        {
            public Task Process(Ask request, CancellationToken cancellationToken)
            {
                trace.Add("before");
                return Acts.Run(request.Before, 0);
            }
        }

        public sealed class After(List<string> trace) : IRequestPostProcessor<Ask, int>
        {
            public Task Process(Ask request, int response, CancellationToken cancellationToken)
            {
                trace.Add($"after {response}");
                return Acts.Run(request.After, 0);
            }
        }

        public static class Acts
        {
            // Released once Send has returned, so what runs before and after that point is always the same.
            public static TaskCompletionSource Gate = new();

            // Set by the handler. An async behaviour puts the caller's value back when it returns.
            public static readonly AsyncLocal<string?> Ambient = new();

            public static Task<int> Run(Act act, int value) => act switch
            {
                Act.Throw => throw new NotSupportedException(),
                Act.Cancel => throw new OperationCanceledException(),
                Act.Yield => Later(value),
                _ => Task.FromResult(value),
            };

            private static async Task<int> Later(int value)
            {
                await Gate.Task;
                return value;
            }
        }

        // Not async, so it sees whatever the processor behaviours inside it do.
        public sealed class Outside<TRequest, TResponse>(List<string> trace) : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
        {
            public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
            {
                try
                {
                    Task<TResponse> task = next(cancellationToken);
                    trace.Add(Acts.Ambient.Value is null ? "task returned" : "task returned, saw " + Acts.Ambient.Value);
                    return task;
                }
                catch (Exception exception)
                {
                    trace.Add("threw " + exception.GetType().Name);
                    throw;
                }
            }
        }

        public static class Program
        {
            public static async Task<string> Run()
            {
                List<string> outcomes = [];
                foreach ((bool pre, Ask ask) in new (bool, Ask)[]
                {
                    (true, new(Act.Answer, Act.Answer, Act.Answer)),
                    (true, new(Act.Throw, Act.Answer, Act.Answer)),
                    (true, new(Act.Cancel, Act.Answer, Act.Answer)),
                    (true, new(Act.Answer, Act.Throw, Act.Answer)),
                    (true, new(Act.Answer, Act.Cancel, Act.Answer)),
                    (true, new(Act.Yield, Act.Yield, Act.Yield)),

                    // Without a pre-processor, the post-processor behaviour is the one the outside behaviour calls.
                    (false, new(Act.Answer, Act.Answer, Act.Answer)),
                    (false, new(Act.Answer, Act.Throw, Act.Answer)),
                    (false, new(Act.Answer, Act.Cancel, Act.Answer)),
                    (false, new(Act.Answer, Act.Answer, Act.Throw)),
                    (false, new(Act.Answer, Act.Answer, Act.Cancel)),
                    (false, new(Act.Answer, Act.Yield, Act.Throw)),
                })
                {
                    List<string> trace = [];
                    ServiceCollection services = new();
                    services.AddSingleton(trace);
                    services.AddTransient(typeof(IPipelineBehavior<,>), typeof(Outside<,>));
                    services.AddRelevox(options =>
                    {
                        if (pre)
                        {
                            options.AddRequestPreProcessor<Before>();
                        }

                        options.AddRequestPostProcessor<After>();
                    });
                    await using ServiceProvider provider = services.BuildServiceProvider();

                    string outcome;
                    Acts.Gate = new();
                    Task<int> task = provider.GetRequiredService<ISender>().Send(ask);
                    Acts.Gate.SetResult();
                    try
                    {
                        outcome = "answered " + await task;
                    }
                    catch (Exception exception)
                    {
                        outcome = task.Status + " " + exception.GetType().Name;
                    }

                    outcomes.Add($"{(pre ? "pre+post" : "post")} {ask.Before}/{ask.Handler}/{ask.After}: {string.Join(", ", trace)}: {outcome}");
                }

                return string.Join("\n", outcomes);
            }
        }
        """;

    [Fact]
    public async Task Every_failure_ends_up_in_the_task_whether_or_not_anything_was_asynchronous()
    {
        string outcomes = await GeneratorHarness.Execute(Program);

        outcomes.Split('\n').ShouldBe(
        [
            "pre+post Answer/Answer/Answer: before, handler, after 42, task returned: answered 42",
            "pre+post Throw/Answer/Answer: before, task returned: Faulted NotSupportedException",
            "pre+post Cancel/Answer/Answer: before, task returned: Canceled OperationCanceledException",
            "pre+post Answer/Throw/Answer: before, handler, task returned: Faulted NotSupportedException",
            "pre+post Answer/Cancel/Answer: before, handler, task returned: Canceled OperationCanceledException",
            "pre+post Yield/Yield/Yield: before, task returned, handler, after 42: answered 42",
            "post Answer/Answer/Answer: handler, after 42, task returned: answered 42",
            "post Answer/Throw/Answer: handler, task returned: Faulted NotSupportedException",
            "post Answer/Cancel/Answer: handler, task returned: Canceled OperationCanceledException",
            "post Answer/Answer/Throw: handler, after 42, task returned: Faulted NotSupportedException",
            "post Answer/Answer/Cancel: handler, after 42, task returned: Canceled OperationCanceledException",
            "post Answer/Yield/Throw: handler, task returned, after 42: Faulted NotSupportedException",
        ]);
    }
}
