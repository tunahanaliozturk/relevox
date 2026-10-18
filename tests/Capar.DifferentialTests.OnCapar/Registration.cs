using Capar.DifferentialTests.OnCapar;
using Microsoft.Extensions.DependencyInjection;

namespace Scenarios;

public static partial class Library
{
    static partial void Register(IServiceCollection services, ServiceLifetime lifetime, Profile profile, Trace trace)
    {
        if (profile == Profile.EarlyBehavior)
        {
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(EarlyBehavior<,>));
        }

        services.AddCapar(options =>
        {
            options.Lifetime = lifetime;

            if (profile == Profile.WhenAll)
            {
                options.NotificationPublisherType = typeof(TaskWhenAllPublisher);
            }

            if (profile == Profile.ReversePublisher)
            {
                options.NotificationPublisher = new ReversePublisher(trace);
            }

            if (profile == Profile.SyncBehavior)
            {
                options.AddBehavior<IPipelineBehavior<Brittle, int>, BrittlePassThrough>();
            }

            if (profile == Profile.AllExceptions)
            {
                options.RequestExceptionActionProcessorStrategy = RequestExceptionActionProcessorStrategy.ApplyForAllExceptions;
            }

            if (profile == Profile.EarlyBehavior)
            {
                options.AddRequestPostProcessor<IRequestPostProcessor<Ping, string>, PingPost>();
            }

            if (profile == Profile.Pipeline)
            {
                options.AutoRegisterRequestProcessors = true;
                options.AddOpenRequestPreProcessor(typeof(StampPre<>));
                options.AddRequestPostProcessor<IRequestPostProcessor<Ping, string>, PingPost>();
                options.AddOpenBehavior(typeof(OuterBehavior<,>));
                options.AddOpenBehavior(typeof(InnerBehavior<,>));
                options.AddOpenBehavior(typeof(AuditedBehavior<,>));
                options.AddBehavior<IPipelineBehavior<Ping, string>, PingAudit>();
                options.AddBehavior<IPipelineBehavior<Cached, string>, CacheBehavior>();
                options.AddBehavior<IPipelineBehavior<Flaky, int>, RetryBehavior>();
                options.AddBehavior<IPipelineBehavior<Retoken, bool>, RetokenBehavior>();
                options.AddBehavior<IPipelineBehavior<Ring, Unit>, RingAudit>();
                options.AddOpenStreamBehavior(typeof(StreamOuter<,>));
                options.AddStreamBehavior<IStreamPipelineBehavior<Countdown, int>, CountdownTimesTen>();
                options.AddStreamBehavior<IStreamPipelineBehavior<Ticker, int>, TickerReplay>();
                options.AddRequestPostProcessor<IRequestPostProcessor<Guarded, string>, GuardedPost>();
            }
        });

        if (profile == Profile.Pipeline)
        {
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(LateBehavior<,>));
        }

        if (profile == Profile.LatePublisher)
        {
            services.AddSingleton<INotificationPublisher>(new ReversePublisher(trace));
        }
    }
}
