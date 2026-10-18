using System.Runtime.CompilerServices;
using Capar;
using Microsoft.Extensions.DependencyInjection;
using Smoke;

// Built against the packed .nupkg files, the way an application that installed them builds. AddCapar has to
// be found with only the Capar and DependencyInjection usings: Smoke is this file's own namespace, not the one
// the generator writes into, so a generated AddCapar that strayed back there would fail this build.
ServiceCollection services = new();
services.AddSingleton<Log>();
services.AddCapar(options => options.AddOpenBehavior(typeof(Counting<,>)));

await using ServiceProvider provider = services.BuildServiceProvider();
using IServiceScope scope = provider.CreateScope();
ISender sender = scope.ServiceProvider.GetRequiredService<ISender>();
Log log = provider.GetRequiredService<Log>();

Check("send through ISender", await sender.Send(new Ping("hi")), "hi pong");
Check("send through the typed mediator", await scope.ServiceProvider.GetRequiredService<global::Capar.PackageSmoke.CaparMediator>().Send(new Ping("typed")), "typed pong");
await sender.Send(new Touch());
Check("send without a response", log.Entries.Contains("touched"), true);
Check("behaviour around every send", log.Entries.Count(entry => entry == "behaviour"), 3);

await scope.ServiceProvider.GetRequiredService<IPublisher>().Publish(new Shipped(7));
Check("publish to both handlers", string.Join(",", log.Entries.Where(entry => entry.StartsWith("shipped", StringComparison.Ordinal))), "shipped-mail 7,shipped-stock 7");

List<int> items = [];
await foreach (int item in sender.CreateStream(new Count(3)))
{
    items.Add(item);
}

Check("stream", string.Join(",", items), "1,2,3");
Console.WriteLine("Every check passed against the packed packages.");
return 0;

static void Check<T>(string what, T actual, T expected)
{
    if (!EqualityComparer<T>.Default.Equals(actual, expected))
    {
        Console.Error.WriteLine($"{what}: expected {expected}, got {actual}");
        Environment.Exit(1);
    }
}

namespace Smoke
{
    public sealed class Log
    {
        public List<string> Entries { get; } = [];
    }

    public sealed record Ping(string Text) : IRequest<string>;

    public sealed class PingHandler : IRequestHandler<Ping, string>
    {
        public Task<string> Handle(Ping request, CancellationToken cancellationToken) => Task.FromResult(request.Text + " pong");
    }

    public sealed record Touch : IRequest;

    public sealed class TouchHandler(Log log) : IRequestHandler<Touch>
    {
        public Task Handle(Touch request, CancellationToken cancellationToken)
        {
            log.Entries.Add("touched");
            return Task.CompletedTask;
        }
    }

    public sealed record Shipped(int OrderId) : INotification;

    public sealed class MailOnShipped(Log log) : INotificationHandler<Shipped>
    {
        public Task Handle(Shipped notification, CancellationToken cancellationToken)
        {
            log.Entries.Add($"shipped-mail {notification.OrderId}");
            return Task.CompletedTask;
        }
    }

    public sealed class StockOnShipped(Log log) : INotificationHandler<Shipped>
    {
        public Task Handle(Shipped notification, CancellationToken cancellationToken)
        {
            log.Entries.Add($"shipped-stock {notification.OrderId}");
            return Task.CompletedTask;
        }
    }

    public sealed record Count(int To) : IStreamRequest<int>;

    public sealed class CountHandler : IStreamRequestHandler<Count, int>
    {
        public async IAsyncEnumerable<int> Handle(Count request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            for (int value = 1; value <= request.To; value++)
            {
                await Task.Yield();
                yield return value;
            }
        }
    }

    public sealed class Counting<TRequest, TResponse>(Log log) : IPipelineBehavior<TRequest, TResponse>
        where TRequest : notnull
    {
        public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        {
            log.Entries.Add("behaviour");
            return next(cancellationToken);
        }
    }
}
