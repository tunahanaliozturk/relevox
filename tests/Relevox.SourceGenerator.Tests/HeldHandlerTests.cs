using System.Globalization;
using System.Text;

namespace Relevox.SourceGenerator.Tests;

/// <summary>
/// Where a resolved handler is held (ADR 0026), on the direct path that only a project without built-in behaviours
/// takes: a singleton once per container, a scoped handler by the mediator of its scope, a transient one nowhere.
/// Twenty handlers, so scoped ones are kept on more than one page of the mediator's storage.
/// </summary>
public sealed class HeldHandlerTests
{
    private const int Count = 20;

    [Fact]
    public async Task Each_handler_is_held_as_its_registration_in_force_says_on_every_send_path()
    {
        string outcome = await GeneratorHarness.Execute(Source());

        // Handler 3 is registered again as a singleton and handler 17 as transient after AddRelevox; the rest are
        // scoped through the option. The notification's two handlers are scoped too.
        string expected = string.Join(",", Enumerable.Range(0, Count).Select(static index => index switch
        {
            3 => "singleton",
            17 => "transient",
            _ => "scoped",
        }));
        outcome.ShouldBe($"{expected} | publish scoped,scoped | other container gets its own singleton: True");
    }

    private static string Source()
    {
        StringBuilder source = new("""
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.Extensions.DependencyInjection;
            using Relevox;

            public static class Instances
            {
                private static int s_next;

                public static int Next() => Interlocked.Increment(ref s_next);
            }

            public sealed record Rang : INotification;

            public sealed class Rang0 : INotificationHandler<Rang>
            {
                public static int Last;
                private readonly int _instance = Instances.Next();

                public Task Handle(Rang notification, CancellationToken cancellationToken)
                {
                    Last = _instance;
                    return Task.CompletedTask;
                }
            }

            public sealed class Rang1 : INotificationHandler<Rang>
            {
                public static int Last;
                private readonly int _instance = Instances.Next();

                public Task Handle(Rang notification, CancellationToken cancellationToken)
                {
                    Last = _instance;
                    return Task.CompletedTask;
                }
            }

            """);

        for (int index = 0; index < Count; index++)
        {
            source.AppendLine(CultureInfo.InvariantCulture, $$"""
                public sealed record Q{{index}} : IRequest<int>;

                public sealed class H{{index}} : IRequestHandler<Q{{index}}, int>
                {
                    private readonly int _instance = Instances.Next();

                    public Task<int> Handle(Q{{index}} request, CancellationToken cancellationToken) => Task.FromResult(_instance);
                }

                """);
        }

        source.AppendLine("""
            public static class Program
            {
                public static async Task<string> Run()
                {
                    ServiceCollection services = new();
                    services.AddRelevox(options => options.HandlerLifetime = ServiceLifetime.Scoped);
                    services.AddSingleton<H3>();
                    services.AddTransient<H17>();
                    await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

                    int[] first = new int[Count];
                    int[] again = new int[Count];
                    int[] next = new int[Count];
                    string publish;
                    using (IServiceScope scope = provider.CreateScope())
                    {
                        Subject.RelevoxMediator typed = scope.ServiceProvider.GetRequiredService<Subject.RelevoxMediator>();
                        ISender sender = scope.ServiceProvider.GetRequiredService<ISender>();
                        for (int index = 0; index < Count; index++)
                        {
                            first[index] = await Typed(typed, index);
                            again[index] = await sender.Send((IRequest<int>)Request(index));
                        }

                        await typed.Publish(new Rang());
                        (int, int) published = (Rang0.Last, Rang1.Last);
                        await ((IPublisher)typed).Publish(new Rang());
                        publish = (published.Item1 == Rang0.Last ? "scoped" : "transient") + "," + (published.Item2 == Rang1.Last ? "scoped" : "transient");
                    }

                    using (IServiceScope scope = provider.CreateScope())
                    {
                        ISender sender = scope.ServiceProvider.GetRequiredService<ISender>();
                        for (int index = 0; index < Count; index++)
                        {
                            next[index] = await sender.Send((IRequest<int>)Request(index));
                        }
                    }

                    string[] kinds = new string[Count];
                    for (int index = 0; index < Count; index++)
                    {
                        kinds[index] = first[index] != again[index] ? "transient" : first[index] == next[index] ? "singleton" : "scoped";
                    }

                    await using ServiceProvider other = services.BuildServiceProvider();
                    bool own = await other.GetRequiredService<ISender>().Send(new Q3()) != first[3];
                    return string.Join(",", kinds) + " | publish " + publish + " | other container gets its own singleton: " + own;
                }

                private const int Count = 20;

                private static object Request(int index) => System.Activator.CreateInstance(typeof(Program).Assembly.GetType("Q" + index)!)!;

                private static Task<int> Typed(Subject.RelevoxMediator mediator, int index) => index switch
                {
            """);
        for (int index = 0; index < Count; index++)
        {
            source.AppendLine(CultureInfo.InvariantCulture, $"            {index} => mediator.Send(new Q{index}()),");
        }

        source.AppendLine("""
                    _ => throw new System.ArgumentOutOfRangeException(nameof(index)),
                };
            }
            """);
        return source.ToString();
    }
}
