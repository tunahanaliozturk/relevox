using Capar.Internal;

namespace Capar.DifferentialTests;

/// <summary>
/// The pipeline chain on its own, past the eight steps that have methods of their own, where the differential
/// scenarios never go.
/// </summary>
public sealed class RequestPipelineTests
{
    [Fact]
    public async Task Ten_behaviours_run_in_order_and_a_late_one_can_retry()
    {
        List<string> trace = [];
        IPipelineBehavior<string, int>[] behaviors =
        [
            .. Enumerable.Range(0, 10).Select(index => (IPipelineBehavior<string, int>)new Recording(index, trace, retry: index == 9)),
        ];

        int handled = 0;
        int result = await RequestPipeline.Run(
            trace,
            "request",
            behaviors,
            (log, request, token) =>
            {
                log.Add("handler");
                return Task.FromResult(++handled);
            },
            CancellationToken.None);

        result.ShouldBe(2);
        trace.ShouldBe([.. Enumerable.Range(0, 10).Select(index => $"b{index}"), "handler", "handler"]);
    }

    [Fact]
    public async Task A_token_passed_to_next_reaches_the_handler_past_the_eighth_step()
    {
        using CancellationTokenSource replacement = new();
        IPipelineBehavior<string, bool>[] behaviors =
        [
            .. Enumerable.Range(0, 9).Select(_ => (IPipelineBehavior<string, bool>)new PassThrough()),
            new Retoken(replacement.Token),
            new PassThrough(),
        ];

        bool sawReplacement = await RequestPipeline.Run(
            replacement.Token,
            "request",
            behaviors,
            (expected, request, token) => Task.FromResult(token == expected),
            CancellationToken.None);

        sawReplacement.ShouldBeTrue();
    }


    [Fact]
    public async Task Skipped_positions_do_not_run_and_a_retry_past_them_still_works()
    {
        List<string> trace = [];
        IPipelineBehavior<string, int>[] behaviors =
        [
            .. Enumerable.Range(0, 10).Select(index => (IPipelineBehavior<string, int>)new Recording(index, trace, retry: index == 8)),
        ];

        int handled = 0;
        int result = await RequestPipeline.Run(
            trace,
            "request",
            behaviors,
            (1UL << 0) | (1UL << 3) | (1UL << 9),
            (log, request, token) =>
            {
                log.Add("handler");
                return Task.FromResult(++handled);
            },
            CancellationToken.None);

        result.ShouldBe(2);
        trace.ShouldBe(["b1", "b2", "b4", "b5", "b6", "b7", "b8", "handler", "handler"]);
    }
    private sealed class Recording(int index, List<string> trace, bool retry) : IPipelineBehavior<string, int>
    {
        public async Task<int> Handle(string request, RequestHandlerDelegate<int> next, CancellationToken cancellationToken)
        {
            trace.Add($"b{index}");
            int first = await next(cancellationToken);
            return retry ? await next(cancellationToken) : first;
        }
    }

    /// <summary>
    /// Forwards the token it was given. Calling next() with no token instead would hand the handler the token
    /// Send was given, not the replacement, which is how MediatR behaves too.
    /// </summary>
    private sealed class PassThrough : IPipelineBehavior<string, bool>
    {
        public Task<bool> Handle(string request, RequestHandlerDelegate<bool> next, CancellationToken cancellationToken) => next(cancellationToken);
    }

    private sealed class Retoken(CancellationToken token) : IPipelineBehavior<string, bool>
    {
        public Task<bool> Handle(string request, RequestHandlerDelegate<bool> next, CancellationToken cancellationToken) => next(token);
    }
}
