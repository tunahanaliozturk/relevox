using Microsoft.Extensions.DependencyInjection;

namespace Scenarios;

public static partial class Library
{
    static partial void Register(IServiceCollection services, ServiceLifetime lifetime, Profile profile)
    {
        services.AddMediatR(configuration =>
        {
            configuration.RegisterServicesFromAssembly(typeof(Library).Assembly);
            configuration.Lifetime = lifetime;

            if (profile == Profile.AllExceptions)
            {
                configuration.RequestExceptionActionProcessorStrategy = RequestExceptionActionProcessorStrategy.ApplyForAllExceptions;
            }

            if (profile == Profile.Pipeline)
            {
                configuration.AutoRegisterRequestProcessors = true;
                configuration.AddOpenRequestPreProcessor(typeof(StampPre<>));
                configuration.AddRequestPostProcessor<IRequestPostProcessor<Ping, string>, PingPost>();
                configuration.AddOpenBehavior(typeof(OuterBehavior<,>));
                configuration.AddOpenBehavior(typeof(InnerBehavior<,>));
                configuration.AddOpenBehavior(typeof(AuditedBehavior<,>));
                configuration.AddBehavior<IPipelineBehavior<Ping, string>, PingAudit>();
                configuration.AddBehavior<IPipelineBehavior<Cached, string>, CacheBehavior>();
                configuration.AddBehavior<IPipelineBehavior<Flaky, int>, RetryBehavior>();
                configuration.AddBehavior<IPipelineBehavior<Retoken, bool>, RetokenBehavior>();
                configuration.AddBehavior<IPipelineBehavior<Ring, Unit>, RingAudit>();
            }
        });

        if (profile == Profile.Pipeline)
        {
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(LateBehavior<,>));
        }
    }
}
