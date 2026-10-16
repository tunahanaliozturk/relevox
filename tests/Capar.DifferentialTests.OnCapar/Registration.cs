using Capar.DifferentialTests.OnCapar;
using Microsoft.Extensions.DependencyInjection;

namespace Scenarios;

public static partial class Library
{
    static partial void Register(IServiceCollection services, ServiceLifetime lifetime, Profile profile)
    {
        services.AddCapar(options =>
        {
            options.Lifetime = lifetime;

            if (profile == Profile.Pipeline)
            {
                options.AddOpenBehavior(typeof(OuterBehavior<,>));
                options.AddOpenBehavior(typeof(InnerBehavior<,>));
                options.AddOpenBehavior(typeof(AuditedBehavior<,>));
                options.AddBehavior<IPipelineBehavior<Ping, string>, PingAudit>();
                options.AddBehavior<IPipelineBehavior<Cached, string>, CacheBehavior>();
                options.AddBehavior<IPipelineBehavior<Flaky, int>, RetryBehavior>();
                options.AddBehavior<IPipelineBehavior<Retoken, bool>, RetokenBehavior>();
                options.AddBehavior<IPipelineBehavior<Ring, Unit>, RingAudit>();
            }
        });

        if (profile == Profile.Pipeline)
        {
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(LateBehavior<,>));
        }
    }
}
