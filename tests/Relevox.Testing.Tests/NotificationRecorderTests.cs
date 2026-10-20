using Microsoft.Extensions.DependencyInjection;
using Relevox.NotificationPublishers;

namespace Relevox.Testing.Tests;

public sealed class NotificationRecorderTests
{
    [Fact]
    public async Task The_real_handlers_run_and_the_recorder_sees_every_notification()
    {
        await using ServiceProvider provider = Build(options => { });
        IPublisher publisher = provider.GetRequiredService<IPublisher>();

        await publisher.Publish(new PriceChanged("bolt", 1.25m));
        await publisher.Publish(new Unheard("nut"));

        provider.GetRequiredService<Ledger>().Lines.ShouldBe(["bolt 1.25"]);
        NotificationRecorder recorder = provider.GetRequiredService<NotificationRecorder>();
        recorder.Published<PriceChanged>().ShouldBe([new PriceChanged("bolt", 1.25m)]);
        recorder.Published<Unheard>().ShouldBe([new Unheard("nut")]);
    }

    [Fact]
    public async Task The_publisher_the_application_chose_still_runs_the_handlers()
    {
        await using ServiceProvider provider = Build(options => options.NotificationPublisherType = typeof(TaskWhenAllPublisher));

        await provider.GetRequiredService<IPublisher>().Publish(new PriceChanged("bolt", 2m));

        provider.GetRequiredService<Ledger>().Lines.ShouldBe(["bolt 2"]);
        provider.GetRequiredService<NotificationRecorder>().Published<object>().Count.ShouldBe(1);
    }

    private static ServiceProvider Build(Action<RelevoxOptions> configure)
    {
        ServiceCollection services = new();
        services.AddSingleton<Ledger>();
        services.AddRelevox(configure);
        services.AddRelevoxNotificationRecorder();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }
}
