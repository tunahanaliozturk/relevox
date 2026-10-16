using Microsoft.Extensions.DependencyInjection;

namespace Capar.Benchmarks;

/// <summary>
/// Builds each library's container the way its own documentation does it.
/// </summary>
/// <remarks>
/// No library gets a handicap or a tuning trick the other does not. Defaults stay defaults: MediatR registers
/// handlers as transient, Mediator as singleton, and that difference is part of what is being compared.
/// Behaviours live in a separate container so the plain send numbers carry no pipeline at all.
/// </remarks>
internal static class Containers
{
    public static ServiceProvider MediatR(bool withPipeline)
    {
        ServiceCollection services = new();
        services.AddMediatR(configuration =>
        {
            configuration.RegisterServicesFromAssemblyContaining<MediatRPeer.Ping>();
            if (withPipeline)
            {
                configuration.AddBehavior<global::MediatR.IPipelineBehavior<MediatRPeer.Piped, int>, MediatRPeer.First>();
                configuration.AddBehavior<global::MediatR.IPipelineBehavior<MediatRPeer.Piped, int>, MediatRPeer.Second>();
                configuration.AddBehavior<global::MediatR.IPipelineBehavior<MediatRPeer.Piped, int>, MediatRPeer.Third>();
            }
        });
        return services.BuildServiceProvider();
    }

    public static ServiceProvider Mediator(bool withPipeline)
    {
        ServiceCollection services = new();
        services.AddMediator();
        if (withPipeline)
        {
            services.AddSingleton<global::Mediator.IPipelineBehavior<MediatorPeer.Piped, int>, MediatorPeer.First>();
            services.AddSingleton<global::Mediator.IPipelineBehavior<MediatorPeer.Piped, int>, MediatorPeer.Second>();
            services.AddSingleton<global::Mediator.IPipelineBehavior<MediatorPeer.Piped, int>, MediatorPeer.Third>();
        }

        return services.BuildServiceProvider();
    }
}
