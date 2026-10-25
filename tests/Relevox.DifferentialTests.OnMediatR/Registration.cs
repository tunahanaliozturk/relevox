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

        if (profile == Profile.EarlySyncBehavior)
        {
            services.AddTransient<IPipelineBehavior<Tripwire, string>, TripwireCatch>();
            services.AddTransient<IPipelineBehavior<Gatekept, string>, Gate>();
        }

        services.AddMediatR(configuration =>
        {
            configuration.RegisterServicesFromAssembly(typeof(Library).Assembly);
            configuration.Lifetime = lifetime;

            if (profile == Profile.WhenAll)
            {
                configuration.NotificationPublisherType = typeof(TaskWhenAllPublisher);
            }

            if (profile == Profile.ReversePublisher)
            {
                configuration.NotificationPublisher = new ReversePublisher(trace);
            }

            if (profile == Profile.SyncBehavior)
            {
                configuration.AddBehavior<IPipelineBehavior<Brittle, int>, BrittlePassThrough>();
            }

            if (profile == Profile.GenericHandlers)
            {
                configuration.RegisterGenericHandlers = true;
            }

            if (profile == Profile.AllExceptions)
            {
                configuration.RequestExceptionActionProcessorStrategy = RequestExceptionActionProcessorStrategy.ApplyForAllExceptions;
            }

            if (profile is Profile.EarlyBehavior or Profile.EarlySyncBehavior)
            {
                configuration.AddRequestPostProcessor<IRequestPostProcessor<Ping, string>, PingPost>();
            }

            if (profile == Profile.Pipeline)
            {
                configuration.AutoRegisterRequestProcessors = true;
                configuration.AddOpenRequestPreProcessor(typeof(StampPre<>));
                configuration.AddRequestPostProcessor<IRequestPostProcessor<Ping, string>, PingPost>();
                configuration.AddOpenBehavior(typeof(OuterBehavior<,>));
                configuration.AddOpenBehavior(typeof(InnerBehavior<,>));
                configuration.AddOpenBehavior(typeof(AuditedBehavior<,>));
                configuration.AddOpenBehavior(typeof(OutcomeBehavior<,>));
                configuration.AddBehavior<IPipelineBehavior<Ping, string>, PingAudit>();
                configuration.AddBehavior<IPipelineBehavior<Cached, string>, CacheBehavior>();
                configuration.AddBehavior<IPipelineBehavior<Flaky, int>, RetryBehavior>();
                configuration.AddBehavior<IPipelineBehavior<Retoken, bool>, RetokenBehavior>();
                configuration.AddBehavior<IPipelineBehavior<Ring, Unit>, RingAudit>();
                configuration.AddOpenStreamBehavior(typeof(StreamOuter<,>));
                configuration.AddStreamBehavior<IStreamPipelineBehavior<Countdown, int>, CountdownTimesTen>();
                configuration.AddStreamBehavior<IStreamPipelineBehavior<Ticker, int>, TickerReplay>();
                configuration.AddRequestPostProcessor<IRequestPostProcessor<Guarded, string>, GuardedPost>();
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
