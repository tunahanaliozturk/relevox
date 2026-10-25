using Microsoft.Extensions.DependencyInjection;
using Relevox.Benchmarks;

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

    /// <summary>Relevox with its default transient handlers, or with singleton handlers to compare like with like.</summary>
    public static ServiceProvider Relevox(ServiceLifetime lifetime, bool withPipeline = false, bool withProcessors = false, bool telemetry = false)
    {
        ServiceCollection services = new();
        services.AddRelevox(options =>
        {
            options.HandlerLifetime = lifetime;
            options.Telemetry.Enabled = telemetry;
            if (withPipeline)
            {
                options.AddBehavior<global::Relevox.IPipelineBehavior<RelevoxPeer.Piped, int>, RelevoxPeer.First>();
                options.AddBehavior<global::Relevox.IPipelineBehavior<RelevoxPeer.Piped, int>, RelevoxPeer.Second>();
                options.AddBehavior<global::Relevox.IPipelineBehavior<RelevoxPeer.Piped, int>, RelevoxPeer.Third>();
            }

            if (withProcessors)
            {
                options.AddRequestPreProcessor<global::Relevox.Pipeline.IRequestPreProcessor<RelevoxPeer.Ping>, RelevoxPeer.PingPre>();
                options.AddRequestPostProcessor<global::Relevox.Pipeline.IRequestPostProcessor<RelevoxPeer.Ping, int>, RelevoxPeer.PingPost>();
            }
        });
        return services.BuildServiceProvider();
    }

    /// <summary>Relevox with the three PipelineOrder behaviours, registered in their order or in reverse.</summary>
    public static ServiceProvider RelevoxOrdered(bool reversed)
    {
        ServiceCollection services = new();
        services.AddRelevox(options =>
        {
            if (reversed)
            {
                options.AddBehavior<global::Relevox.IPipelineBehavior<RelevoxPeer.Piped, int>, RelevoxPeer.OrderedThird>();
                options.AddBehavior<global::Relevox.IPipelineBehavior<RelevoxPeer.Piped, int>, RelevoxPeer.OrderedSecond>();
                options.AddBehavior<global::Relevox.IPipelineBehavior<RelevoxPeer.Piped, int>, RelevoxPeer.OrderedFirst>();
            }
            else
            {
                options.AddBehavior<global::Relevox.IPipelineBehavior<RelevoxPeer.Piped, int>, RelevoxPeer.OrderedFirst>();
                options.AddBehavior<global::Relevox.IPipelineBehavior<RelevoxPeer.Piped, int>, RelevoxPeer.OrderedSecond>();
                options.AddBehavior<global::Relevox.IPipelineBehavior<RelevoxPeer.Piped, int>, RelevoxPeer.OrderedThird>();
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
