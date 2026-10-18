extern alias oncapar;

using Microsoft.Extensions.DependencyInjection;
using CaparMediator = oncapar::Capar.DifferentialTests.OnCapar.CaparMediator;
using Registration = oncapar::Microsoft.Extensions.DependencyInjection.CaparServiceCollectionExtensions;
using S = oncapar::Scenarios;

namespace Capar.DifferentialTests;

/// <summary>
/// Behaviour MediatR has no equivalent for, so there is nothing to compare against: the typed overloads on
/// the generated mediator, handler lifetimes, and what happens when two projects both call AddCapar.
/// </summary>
public sealed class CaparOnlyTests
{
    [Fact]
    public async Task A_typed_overload_goes_by_the_runtime_type_not_the_static_one()
    {
        await using ServiceProvider provider = Build();
        CaparMediator mediator = provider.GetRequiredService<CaparMediator>();

        // Binds to Send(BaseQuery) at compile time. The value is a DerivedQuery, which has no handler of its
        // own; MediatR and the interface path both throw for it, and the typed path has to agree.
        S.BaseQuery query = new S.DerivedQuery();

        await Should.ThrowAsync<InvalidOperationException>(() => mediator.Send(query));
        (await mediator.Send(new S.BaseQuery())).ShouldBe("base");
    }

    [Fact]
    public async Task Singleton_handlers_are_resolved_once_and_kept()
    {
        await using ServiceProvider provider = Build(options => options.HandlerLifetime = ServiceLifetime.Singleton);
        CaparMediator mediator = provider.GetRequiredService<CaparMediator>();

        (await mediator.Send(new S.Stamp())).ShouldBe(await mediator.Send(new S.Stamp()));
    }

    [Fact]
    public async Task A_handler_the_application_registered_as_transient_is_never_kept()
    {
        // The application's own registration wins over the option, because AddCapar only adds what is missing,
        // and a kept transient handler would quietly become a shared one.
        await using ServiceProvider provider = Build(
            options => options.HandlerLifetime = ServiceLifetime.Singleton,
            services => services.AddTransient<S.StampHandler>());
        CaparMediator mediator = provider.GetRequiredService<CaparMediator>();

        (await mediator.Send(new S.Stamp())).ShouldNotBe(await mediator.Send(new S.Stamp()));
    }

    [Fact]
    public void Calling_AddCapar_twice_is_harmless()
    {
        ServiceCollection services = new();
        Registration.AddCapar(services);
        int count = services.Count;

        Registration.AddCapar(services);

        services.Count.ShouldBe(count);
    }

    [Fact]
    public void AddCapar_refuses_to_run_after_another_mediator_was_registered()
    {
        ServiceCollection services = new();
        services.AddSingleton<global::Capar.IMediator>(_ => throw new NotSupportedException());

        InvalidOperationException refused = Should.Throw<InvalidOperationException>(() => Registration.AddCapar(services));
        refused.Message.ShouldContain("Capar.Contracts");
    }

    [Fact]
    public async Task Seventy_behaviours_with_idle_and_active_built_ins_all_run()
    {
        // A long pipeline, so positions are not accidentally right: idle built-ins are left out, active ones kept.
        await using ServiceProvider provider = Build(
            options => options.AddRequestPostProcessor<global::Capar.Pipeline.IRequestPostProcessor<S.Ping, string>, S.PingPost>(),
            before: services =>
            {
                for (int index = 0; index < 70; index++)
                {
                    services.AddTransient(typeof(IPipelineBehavior<,>), typeof(S.EarlyBehavior<,>));
                }
            });

        S.Trace trace = provider.GetRequiredService<S.Trace>();
        ISender sender = provider.GetRequiredService<ISender>();

        for (int round = 0; round < 2; round++)
        {
            await sender.Send(new S.Ring(1));
            (await sender.Send(new S.Ping("deep"))).ShouldBe("deep pong");
        }

        trace.Events.Count(entry => entry == "Early>").ShouldBe(4 * 70);
        trace.Events.Count(entry => entry.StartsWith("PingPost", StringComparison.Ordinal)).ShouldBe(2);
    }

    [Fact]
    public void A_built_in_slot_refuses_to_run_outside_the_generated_mediator()
    {
        global::Capar.Internal.PreProcessorSlot<S.Ping, string> slot = new();

        Should.Throw<NotSupportedException>(() => slot.Handle(new S.Ping("x"), _ => Task.FromResult("y"), CancellationToken.None));
    }
    [Fact]
    public void A_scope_that_returns_more_behaviours_than_the_plan_was_learned_from_still_runs_them_all()
    {
        // Containers such as Autofac let a child scope register behaviours of its own, so the same request can
        // come back with a longer list than the one its first send saw.
        global::Capar.Internal.PipelineState<CaparOnlyTests> state = new(1, 0, builtInsForEveryRequest: true);
        S.Trace trace = new();
        IPipelineBehavior<S.Ping, string> slot = new global::Capar.Internal.PostProcessorSlot<S.Ping, string>();
        IPipelineBehavior<S.Ping, string> early = new S.EarlyBehavior<S.Ping, string>(trace);

        IPipelineBehavior<S.Ping, string>[]? first = state.Behaviors<S.Ping, string>(0, new Fixed([early, slot]), out _);
        IPipelineBehavior<S.Ping, string>[]? second = state.Behaviors<S.Ping, string>(0, new Fixed([early, early, slot]), out _);

        first!.Length.ShouldBe(2);
        second!.Length.ShouldBe(3);
        second.ShouldNotContain(behavior => behavior is global::Capar.Internal.BuiltInSlot<S.Ping, string>);
    }

    /// <summary>A container that hands out one fixed list of behaviours and no processors.</summary>
    private sealed class Fixed(IPipelineBehavior<S.Ping, string>[] behaviors) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IEnumerable<IPipelineBehavior<S.Ping, string>>) ? behaviors
            : serviceType == typeof(IEnumerable<global::Capar.Pipeline.IRequestPostProcessor<S.Ping, string>>) ? Array.Empty<global::Capar.Pipeline.IRequestPostProcessor<S.Ping, string>>()
            : null;
    }

    [Theory]
    [InlineData(S.Profile.Plain)]
    [InlineData(S.Profile.Pipeline)]
    public async Task Stack_traces_show_handlers_and_behaviours_but_not_the_plumbing_between_them(S.Profile profile)
    {
        // What a developer reads first when something fails. The generated mediator and Capar.Internal are hidden
        // from stack traces and stepped through by the debugger; handlers, behaviours and processors are not.
        await using ServiceProvider provider = S.Library.Build(profile: profile);
        using IServiceScope scope = provider.CreateScope();
        ISender sender = scope.ServiceProvider.GetRequiredService<ISender>();
        CaparMediator typed = scope.ServiceProvider.GetRequiredService<CaparMediator>();

        string[] traces =
        [
            await StackOf(() => sender.Send(new S.Explode())),
            await StackOf(() => typed.Send(new S.Explode())),
            await StackOf(() => sender.Send((object)new S.Explode())),
            await StackOf(() => scope.ServiceProvider.GetRequiredService<IPublisher>().Publish(new S.Boom())),
            await StackOf(async () =>
            {
                await foreach (int item in sender.CreateStream(new S.Sputter()))
                {
                    _ = item;
                }
            }),
        ];

        traces[0].ShouldContain("ExplodeHandler.Handle");
        traces[3].ShouldContain("BoomHandler.Handle");
        traces[4].ShouldContain("SputterHandler.Handle");
        foreach (string trace in traces)
        {
            trace.ShouldNotContain("Capar.Internal");
        }

        // The typed, interface and stream paths show nothing of the mediator. Send(object) and the default
        // publisher keep one frame each: they are async methods, and .NET checks StackTraceHidden on the state
        // machine the compiler generates, which no attribute can reach.
        foreach (int path in new[] { 0, 1, 4 })
        {
            traces[path].ShouldNotContain("CaparMediator");
        }

        foreach (int path in new[] { 2, 3 })
        {
            traces[path].Split(Environment.NewLine).Count(line => line.Contains("CaparMediator", StringComparison.Ordinal)).ShouldBe(1);
        }

        if (profile == S.Profile.Pipeline)
        {
            traces[0].ShouldContain("OuterBehavior");
        }
    }

    private static async Task<string> StackOf(Func<Task> call)
    {
        try
        {
            await call();
        }
        catch (Exception exception) when (exception is NotSupportedException or InvalidOperationException)
        {
            return exception.StackTrace ?? string.Empty;
        }

        throw new InvalidOperationException("Expected the call to fail.");
    }

    private static ServiceProvider Build(Action<global::Capar.CaparOptions>? configure = null, Action<IServiceCollection>? before = null)
    {
        ServiceCollection services = new();
        services.AddSingleton<S.Trace>();
        before?.Invoke(services);
        Registration.AddCapar(services, configure);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }
}
