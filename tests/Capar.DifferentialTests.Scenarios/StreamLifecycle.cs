using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace Scenarios;

// The stream enumerator's life cycle, which M4's hand-written enumerator has to keep identical to MediatR's
// async iterator: two cancellable tokens, disposing early or twice, and asking for more after a failure.

/// <summary>Yields until the token it was handed is cancelled, then says so.</summary>
public sealed record Watch : IStreamRequest<int>;

public sealed class WatchHandler(Trace trace) : IStreamRequestHandler<Watch, int>
{
    public async IAsyncEnumerable<int> Handle(Watch request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (int value = 1; value <= 10; value++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                trace.Add($"WatchHandler stopped before {value}");
                yield break;
            }

            yield return value;
        }

        await Task.CompletedTask;
    }
}

/// <summary>Returns an enumerator of its own that records every DisposeAsync.</summary>
public sealed record Counted : IStreamRequest<int>;

public sealed class CountedHandler(Trace trace) : IStreamRequestHandler<Counted, int>
{
    public IAsyncEnumerable<int> Handle(Counted request, CancellationToken cancellationToken) => new Items(trace);

    private sealed class Items(Trace trace) : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        private int _current;

        public int Current => _current;

        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;

        public ValueTask<bool> MoveNextAsync() => new(++_current <= 2);

        public ValueTask DisposeAsync()
        {
            trace.Add("Counted disposed");
            return default;
        }
    }
}

public static partial class Scenarios
{
    public static Task<string> StreamWithTwoTokensStopsOnTheCreationToken() =>
        Run.Scenario(services => TwoTokens(services, cancelCreation: true, boxed: false));

    public static Task<string> StreamWithTwoTokensStopsOnTheEnumerationToken() =>
        Run.Scenario(services => TwoTokens(services, cancelCreation: false, boxed: false));

    public static Task<string> StreamObjectWithTwoTokensStopsOnTheEnumerationToken() =>
        Run.Scenario(services => TwoTokens(services, cancelCreation: false, boxed: true));

    public static Task<string> StreamDisposedBeforeItStarts() =>
        Run.Scenario(async services =>
        {
            IAsyncEnumerator<int> enumerator = services.GetRequiredService<ISender>().CreateStream(new Counted()).GetAsyncEnumerator();
            await enumerator.DisposeAsync();
            bool more = await enumerator.MoveNextAsync();
            return $"after dispose: {more}";
        });

    public static Task<string> StreamDisposedTwice() =>
        Run.Scenario(async services =>
        {
            IAsyncEnumerator<int> enumerator = services.GetRequiredService<ISender>().CreateStream(new Counted()).GetAsyncEnumerator();
            bool first = await enumerator.MoveNextAsync();
            await enumerator.DisposeAsync();
            await enumerator.DisposeAsync();
            bool more = await enumerator.MoveNextAsync();
            return $"first: {first}, after dispose: {more}, current: {enumerator.Current}";
        });

    public static Task<string> StreamAskedForMoreAfterItFailed() =>
        Run.Scenario(async services =>
        {
            IAsyncEnumerator<int> enumerator = services.GetRequiredService<ISender>().CreateStream(new Silence()).GetAsyncEnumerator();
            string first;
            try
            {
                await enumerator.MoveNextAsync();
                first = "no failure";
            }
            catch (Exception exception)
            {
                first = exception.GetType().Name;
            }

            bool more = await enumerator.MoveNextAsync();
            await enumerator.DisposeAsync();
            return $"first: {first}, then: {more}";
        });

    private static async Task<object?> TwoTokens(IServiceProvider services, bool cancelCreation, bool boxed)
    {
        using CancellationTokenSource creation = new();
        using CancellationTokenSource enumeration = new();
        ISender sender = services.GetRequiredService<ISender>();
        CancellationTokenSource toCancel = cancelCreation ? creation : enumeration;

        return boxed
            ? await Drain(sender.CreateStream((object)new Watch(), creation.Token), toCancel, enumeration.Token)
            : await Drain(sender.CreateStream(new Watch(), creation.Token), toCancel, enumeration.Token);
    }

    /// <summary>Enumerates with a token of its own and cancels one of the two tokens after the second item.</summary>
    private static async Task<string> Drain<T>(IAsyncEnumerable<T> stream, CancellationTokenSource toCancel, CancellationToken enumeration)
    {
        List<string> items = [];
        await foreach (T item in stream.WithCancellation(enumeration))
        {
            items.Add($"{item}");
            if (items.Count == 2)
            {
                await toCancel.CancelAsync();
            }
        }

        return string.Join(",", items);
    }
}
