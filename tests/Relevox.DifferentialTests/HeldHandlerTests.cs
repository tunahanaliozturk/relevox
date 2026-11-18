extern alias onrelevox;
using Microsoft.Extensions.DependencyInjection;
using Registration = onrelevox::Microsoft.Extensions.DependencyInjection.RelevoxServiceCollectionExtensions;
using RelevoxMediator = onrelevox::Relevox.DifferentialTests.OnRelevox.RelevoxMediator;
using S = onrelevox::Scenarios;

namespace Relevox.DifferentialTests;

/// <summary>
/// Where a resolved handler is held (ADR 0026): a singleton once per container, so a mediator built for each scope
/// does not ask for it again, a scoped handler by the mediator of its scope, and a transient one nowhere.
/// </summary>
public sealed class HeldHandlerTests
{
    [Fact]
    public async Task Two_containers_built_from_one_collection_do_not_share_a_singleton_handler()
    {
        // The same collection, so the same AddRelevox call and its factories: what is held must still belong to
        // the container that built the mediator.
        ServiceCollection services = Services(options => options.HandlerLifetime = ServiceLifetime.Singleton);
        await using ServiceProvider first = services.BuildServiceProvider();
        await using ServiceProvider second = services.BuildServiceProvider();

        int fromFirst = await first.GetRequiredService<ISender>().Send(new S.Stamp());
        int fromSecond = await second.GetRequiredService<ISender>().Send(new S.Stamp());

        fromSecond.ShouldNotBe(fromFirst);
        (await first.GetRequiredService<ISender>().Send(new S.Stamp())).ShouldBe(fromFirst);
        (await second.GetRequiredService<ISender>().Send(new S.Stamp())).ShouldBe(fromSecond);
    }

    [Fact]
    public async Task A_singleton_handler_is_held_for_the_container_not_for_each_mediator()
    {
        ServiceCollection services = Services(options => options.HandlerLifetime = ServiceLifetime.Singleton);
        await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using (IServiceScope earlier = provider.CreateScope())
        {
            await earlier.ServiceProvider.GetRequiredService<ISender>().Send(new S.Tally("first"));
        }

        using IServiceScope scope = provider.CreateScope();
        CountingProvider counting = new(scope.ServiceProvider);
        RelevoxMediator mediator = Mediator(services, counting);
        await mediator.Send(new S.Tally("typed"));
        await ((ISender)mediator).Send(new S.Tally("through ISender"));

        // Building the mediator asks for what the container holds; the handler was resolved by the mediator of the
        // earlier scope and is not asked for again.
        counting.Requested.ShouldBe([typeof(RelevoxMediator.HeldHandlers)]);
    }

    [Fact]
    public async Task A_scoped_handler_is_held_by_the_mediator_of_its_scope_and_follows_that_scope()
    {
        ServiceCollection services = Services(options => options.HandlerLifetime = ServiceLifetime.Scoped);
        await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        using IServiceScope scope = provider.CreateScope();
        CountingProvider counting = new(scope.ServiceProvider);
        RelevoxMediator mediator = Mediator(services, counting);
        int first = await mediator.Send(new S.Stamp());
        counting.Requested.Clear();
        int again = await mediator.Send(new S.Stamp());

        counting.Requested.ShouldBeEmpty();
        again.ShouldBe(first);
        (await scope.ServiceProvider.GetRequiredService<ISender>().Send(new S.Stamp())).ShouldBe(first);

        using IServiceScope other = provider.CreateScope();
        (await other.ServiceProvider.GetRequiredService<ISender>().Send(new S.Stamp())).ShouldNotBe(first);
    }

    /// <summary>
    /// Pins down what MediatR, the oracle, does with a handler registered as scoped, so that the differential test
    /// on these scenarios cannot pass with both libraries getting it wrong the same way.
    /// </summary>
    [Theory]
    [InlineData("HandlerRegisteredScopedFollowsItsScope")]
    [InlineData("HandlerRegisteredScopedFollowsTheScopeOfAScopedMediator")]
    public async Task MediatR_gives_a_handler_registered_as_scoped_one_instance_per_scope(string scenario)
    {
        string outcome = await DifferentialTests.Invoke(DifferentialTests.OnMediatR, scenario);

        outcome.ShouldContain("the same instance within the scope, another in the next scope");
    }

    [Fact]
    public async Task A_transient_handler_is_resolved_on_every_send()
    {
        ServiceCollection services = Services();
        await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        using IServiceScope scope = provider.CreateScope();
        CountingProvider counting = new(scope.ServiceProvider);
        RelevoxMediator mediator = Mediator(services, counting);
        int first = await mediator.Send(new S.Stamp());
        counting.Requested.Clear();
        int second = await mediator.Send(new S.Stamp());

        counting.Requested.ShouldBe([typeof(S.StampHandler)]);
        second.ShouldNotBe(first);
    }

    [Fact]
    public async Task A_handler_registered_again_as_singleton_after_AddRelevox_is_held_for_the_container()
    {
        // The last registration is the one in force, here and in the container.
        ServiceCollection services = Services();
        services.AddSingleton<S.StampHandler>();
        await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        int first;
        using (IServiceScope scope = provider.CreateScope())
        {
            first = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new S.Stamp());
        }

        using IServiceScope later = provider.CreateScope();
        (await later.ServiceProvider.GetRequiredService<ISender>().Send(new S.Stamp())).ShouldBe(first);
        later.ServiceProvider.GetRequiredService<S.StampHandler>().ShouldBeSameAs(provider.GetRequiredService<S.StampHandler>());
    }

    private static ServiceCollection Services(Action<global::Microsoft.Extensions.DependencyInjection.RelevoxOptions>? configure = null)
    {
        ServiceCollection services = new();
        services.AddSingleton<S.Trace>();
        Registration.AddRelevox(services, configure);
        return services;
    }

    /// <summary>A mediator made by the factory <c>AddRelevox</c> registered, resolving through the provider given.</summary>
    private static RelevoxMediator Mediator(ServiceCollection services, IServiceProvider provider) =>
        (RelevoxMediator)services.Single(descriptor => descriptor.ServiceType == typeof(RelevoxMediator)).ImplementationFactory!(provider);

    private sealed class CountingProvider(IServiceProvider inner) : IServiceProvider
    {
        public List<Type> Requested { get; } = [];

        public object? GetService(Type serviceType)
        {
            Requested.Add(serviceType);
            return inner.GetService(serviceType);
        }
    }
}
