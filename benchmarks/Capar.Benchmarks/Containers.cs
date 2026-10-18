using Capar.Benchmarks;
using Microsoft.Extensions.DependencyInjection;

namespace Benchmarks;

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
    public static ServiceProvider MediatR(bool withPipeline, bool withProcessors = false)
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

            if (withProcessors)
            {
                configuration.AddRequestPreProcessor<global::MediatR.Pipeline.IRequestPreProcessor<MediatRPeer.Ping>, MediatRPeer.PingPre>();
                configuration.AddRequestPostProcessor<global::MediatR.Pipeline.IRequestPostProcessor<MediatRPeer.Ping, int>, MediatRPeer.PingPost>();
            }
        });
        return services.BuildServiceProvider();
    }

    /// <summary>Capar with its default transient handlers, or with singleton handlers to compare like with like.</summary>
    public static ServiceProvider Capar(ServiceLifetime lifetime, bool withPipeline = false, bool withProcessors = false)
    {
        ServiceCollection services = new();
        services.AddCapar(options =>
        {
            options.HandlerLifetime = lifetime;
            if (withPipeline)
            {
                options.AddBehavior<global::Capar.IPipelineBehavior<CaparPeer.Piped, int>, CaparPeer.First>();
                options.AddBehavior<global::Capar.IPipelineBehavior<CaparPeer.Piped, int>, CaparPeer.Second>();
                options.AddBehavior<global::Capar.IPipelineBehavior<CaparPeer.Piped, int>, CaparPeer.Third>();
            }

            if (withProcessors)
            {
                options.AddRequestPreProcessor<global::Capar.Pipeline.IRequestPreProcessor<CaparPeer.Ping>, CaparPeer.PingPre>();
                options.AddRequestPostProcessor<global::Capar.Pipeline.IRequestPostProcessor<CaparPeer.Ping, int>, CaparPeer.PingPost>();
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
