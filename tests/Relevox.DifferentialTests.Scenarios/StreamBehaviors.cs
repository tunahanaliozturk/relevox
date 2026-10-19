using System.Runtime.CompilerServices;

namespace Scenarios;

// Stream behaviours, registered under Profile.Pipeline.

/// <summary>Open: wraps every stream and marks where it starts and ends.</summary>
public sealed class StreamOuter<TRequest, TResponse>(Trace trace) : IStreamPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async IAsyncEnumerable<TResponse> Handle(
        TRequest request,
        StreamHandlerDelegate<TResponse> next,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        trace.Add($"StreamOuter>{typeof(TRequest).Name}");
        await foreach (TResponse item in next().WithCancellation(cancellationToken))
        {
            yield return item;
        }

        trace.Add("StreamOuter<");
    }
}

/// <summary>Closed: multiplies every item of a countdown by ten.</summary>
public sealed class CountdownTimesTen(Trace trace) : IStreamPipelineBehavior<Countdown, int>
{
    public async IAsyncEnumerable<int> Handle(
        Countdown request,
        StreamHandlerDelegate<int> next,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        trace.Add("CountdownTimesTen");
        await foreach (int item in next().WithCancellation(cancellationToken))
        {
            yield return item * 10;
        }
    }
}

/// <summary>Closed: answers a ticker from a fixed list without calling the rest of the pipeline.</summary>
public sealed class TickerReplay(Trace trace) : IStreamPipelineBehavior<Ticker, int>
{
    public async IAsyncEnumerable<int> Handle(
        Ticker request,
        StreamHandlerDelegate<int> next,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        trace.Add("TickerReplay");
        yield return -1;
        yield return -2;
        await Task.CompletedTask;
    }
}
