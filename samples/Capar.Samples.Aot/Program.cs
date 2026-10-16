using System.Runtime.CompilerServices;
using Capar;
using Capar.Pipeline;
using Capar.Samples.Aot;
using Microsoft.Extensions.DependencyInjection;

// Every kind of dispatch Capar generates, in a NativeAOT binary. Each line checks its own answer; the process
// exits with 1 on the first wrong one, so CI fails on a wrong result as well as on a build warning.

ServiceCollection services = new();
services.AddSingleton<Journal>();
services.AddCapar(options =>
{
    options.AddBehavior<IPipelineBehavior<Quote, decimal>, Rounding>();
    options.AddRequestPreProcessor<IRequestPreProcessor<Quote>, QuoteCheck>();
});

await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
await using AsyncServiceScope scope = provider.CreateAsyncScope();
Journal journal = scope.ServiceProvider.GetRequiredService<Journal>();
CaparMediator typed = scope.ServiceProvider.GetRequiredService<CaparMediator>();
ISender sender = scope.ServiceProvider.GetRequiredService<ISender>();
IPublisher publisher = scope.ServiceProvider.GetRequiredService<IPublisher>();

Check("typed send", await typed.Send(new Greet("aot")), "hello aot");
Check("ISender send", await sender.Send(new Greet("interface")), "hello interface");
Check("Send(object)", await sender.Send((object)new Greet("object")), "hello object");

await sender.Send(new Archive(7));
Check("request without response", journal.Last, "archived 7");

Check("pipeline behaviour and pre-processor", await sender.Send(new Quote(10.004m)), 10.00m);
Check("pre-processor ran", journal.Contains("checked 10.004"), true);

Check("exception handler", await sender.Send(new Quote(-1m)), 0m);

await publisher.Publish(new Shipped(3));
Check("notification, first handler", journal.Contains("emailed 3"), true);
Check("notification, second handler", journal.Contains("invoiced 3"), true);

List<int> items = [];
await foreach (int item in sender.CreateStream(new Countdown(3)))
{
    items.Add(item);
}

Check("stream", string.Join(",", items), "3,2,1");

Console.WriteLine("All checks passed.");
return 0;

static void Check<T>(string what, T actual, T expected)
{
    if (!EqualityComparer<T>.Default.Equals(actual, expected))
    {
        Console.Error.WriteLine($"FAIL {what}: expected {expected}, got {actual}");
        Environment.Exit(1);
    }

    Console.WriteLine($"ok   {what}");
}

namespace Capar.Samples.Aot
{
    public sealed class Journal
    {
        private readonly List<string> _entries = [];

        public string Last => _entries[^1];

        public void Write(string entry) => _entries.Add(entry);

        public bool Contains(string entry) => _entries.Contains(entry);
    }

    public sealed record Greet(string Name) : IRequest<string>;

    public sealed class GreetHandler : IRequestHandler<Greet, string>
    {
        public Task<string> Handle(Greet request, CancellationToken cancellationToken) => Task.FromResult($"hello {request.Name}");
    }

    public sealed record Archive(int Id) : IRequest;

    public sealed class ArchiveHandler(Journal journal) : IRequestHandler<Archive>
    {
        public Task Handle(Archive request, CancellationToken cancellationToken)
        {
            journal.Write($"archived {request.Id}");
            return Task.CompletedTask;
        }
    }

    public sealed record Quote(decimal Amount) : IRequest<decimal>;

    public sealed class QuoteHandler : IRequestHandler<Quote, decimal>
    {
        public Task<decimal> Handle(Quote request, CancellationToken cancellationToken) =>
            request.Amount < 0 ? throw new ArgumentOutOfRangeException(nameof(request)) : Task.FromResult(request.Amount);
    }

    public sealed class QuoteCheck(Journal journal) : IRequestPreProcessor<Quote>
    {
        public Task Process(Quote request, CancellationToken cancellationToken)
        {
            journal.Write($"checked {request.Amount}");
            return Task.CompletedTask;
        }
    }

    public sealed class Rounding : IPipelineBehavior<Quote, decimal>
    {
        public async Task<decimal> Handle(Quote request, RequestHandlerDelegate<decimal> next, CancellationToken cancellationToken) =>
            Math.Round(await next(cancellationToken), 2);
    }

    public sealed class NegativeQuote : IRequestExceptionHandler<Quote, decimal, ArgumentException>
    {
        public Task Handle(Quote request, ArgumentException exception, RequestExceptionHandlerState<decimal> state, CancellationToken cancellationToken)
        {
            state.SetHandled(0m);
            return Task.CompletedTask;
        }
    }

    public sealed record Shipped(int OrderId) : INotification;

    public sealed class EmailCustomer(Journal journal) : INotificationHandler<Shipped>
    {
        public Task Handle(Shipped notification, CancellationToken cancellationToken)
        {
            journal.Write($"emailed {notification.OrderId}");
            return Task.CompletedTask;
        }
    }

    public sealed class Invoice(Journal journal) : INotificationHandler<Shipped>
    {
        public Task Handle(Shipped notification, CancellationToken cancellationToken)
        {
            journal.Write($"invoiced {notification.OrderId}");
            return Task.CompletedTask;
        }
    }

    public sealed record Countdown(int From) : IStreamRequest<int>;

    public sealed class CountdownHandler : IStreamRequestHandler<Countdown, int>
    {
        public async IAsyncEnumerable<int> Handle(Countdown request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            for (int value = request.From; value > 0; value--)
            {
                yield return value;
            }

            await Task.CompletedTask;
        }
    }
}
