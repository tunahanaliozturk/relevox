extern alias onrelevox;
using Microsoft.Extensions.DependencyInjection;
using Registration = onrelevox::Microsoft.Extensions.DependencyInjection.RelevoxServiceCollectionExtensions;
using RelevoxMediator = onrelevox::Relevox.DifferentialTests.OnRelevox.RelevoxMediator;
using S = onrelevox::Scenarios;

namespace Relevox.DifferentialTests;

/// <summary>
/// Behaviour MediatR has no equivalent for, so there is nothing to compare against: the typed overloads on
/// the generated mediator, handler lifetimes, and what happens when two projects both call AddRelevox.
/// </summary>
public sealed class RelevoxOnlyTests
{
    [Fact]
    public async Task A_typed_overload_goes_by_the_runtime_type_not_the_static_one()
    {
        await using ServiceProvider provider = Build();
        RelevoxMediator mediator = provider.GetRequiredService<RelevoxMediator>();

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
        RelevoxMediator mediator = provider.GetRequiredService<RelevoxMediator>();

        (await mediator.Send(new S.Stamp())).ShouldBe(await mediator.Send(new S.Stamp()));
    }

    [Fact]
    public async Task A_handler_the_application_registered_as_transient_is_never_kept()
    {
        // The application's own registration wins over the option, because AddRelevox only adds what is missing,
        // and a kept transient handler would quietly become a shared one.
        await using ServiceProvider provider = Build(
            options => options.HandlerLifetime = ServiceLifetime.Singleton,
            services => services.AddTransient<S.StampHandler>());
        RelevoxMediator mediator = provider.GetRequiredService<RelevoxMediator>();

        (await mediator.Send(new S.Stamp())).ShouldNotBe(await mediator.Send(new S.Stamp()));
    }

    [Fact]
    public async Task A_handler_registered_again_as_transient_after_AddRelevox_is_never_kept()
    {
        // The container resolves the last registration, so the mediator has to judge by it as well.
        ServiceCollection services = new();
        services.AddSingleton<S.Trace>();
        Registration.AddRelevox(services, options => options.HandlerLifetime = ServiceLifetime.Singleton);
        services.AddTransient<S.StampHandler>();
        await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        RelevoxMediator mediator = provider.GetRequiredService<RelevoxMediator>();

        (await mediator.Send(new S.Stamp())).ShouldNotBe(await mediator.Send(new S.Stamp()));
    }

    [Fact]
    public void Calling_AddRelevox_twice_is_harmless()
    {
        ServiceCollection services = new();
        Registration.AddRelevox(services);
        int count = services.Count;

        Registration.AddRelevox(services);

        services.Count.ShouldBe(count);
    }

    [Fact]
    public void AddRelevox_refuses_to_run_after_another_generated_mediator_was_registered()
    {
        // What a second project's AddRelevox leaves behind: its mediator's pipeline state.
        ServiceCollection services = new();
        services.AddSingleton(_ => new global::Relevox.Internal.PipelineState<RelevoxOnlyTests>(1, 0, builtInsForEveryRequest: false));

        InvalidOperationException refused = Should.Throw<InvalidOperationException>(() => Registration.AddRelevox(services));
        refused.Message.ShouldContain("Relevox.Contracts");
    }

    [Fact]
    public async Task An_IMediator_the_application_registered_first_is_kept()
    {
        // A test double for IMediator, registered before AddRelevox, as MediatR keeps it. ISender, which the
        // application did not register, still comes from the generated mediator.
        await using ServiceProvider provider = Build(before: services => services.AddSingleton<global::Relevox.IMediator, Stand>());

        provider.GetRequiredService<global::Relevox.IMediator>().ShouldBeOfType<Stand>();
        (await provider.GetRequiredService<ISender>().Send(new S.Ping("real"))).ShouldBe("real pong");
    }

    private sealed class Stand : global::Relevox.IMediator
    {
        public Task<TResponse> Send<TResponse>(global::Relevox.IRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : global::Relevox.IRequest => throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(global::Relevox.IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : global::Relevox.INotification => throw new NotSupportedException();
    }

    [Fact]
    public async Task Seventy_behaviours_with_idle_and_active_built_ins_all_run()
    {
        // A long pipeline, so positions are not accidentally right: idle built-ins are left out, active ones kept.
        await using ServiceProvider provider = Build(
            options => options.AddRequestPostProcessor<global::Relevox.Pipeline.IRequestPostProcessor<S.Ping, string>, S.PingPost>(),
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
        global::Relevox.Internal.PreProcessorSlot<S.Ping, string> slot = new();

        Should.Throw<NotSupportedException>(() => slot.Handle(new S.Ping("x"), _ => Task.FromResult("y"), CancellationToken.None));
    }
    [Fact]
    public void A_scope_that_returns_more_behaviours_than_the_plan_was_learned_from_still_runs_them_all()
    {
        // Containers such as Autofac let a child scope register behaviours of its own, so the same request can
        // come back with a longer list than the one its first send saw.
        global::Relevox.Internal.PipelineState<RelevoxOnlyTests> state = new(1, 0, builtInsForEveryRequest: true);
        S.Trace trace = new();
        IPipelineBehavior<S.Ping, string> slot = new global::Relevox.Internal.PostProcessorSlot<S.Ping, string>();
        IPipelineBehavior<S.Ping, string> early = new S.EarlyBehavior<S.Ping, string>(trace);

        IPipelineBehavior<S.Ping, string>[]? first = state.Behaviors<S.Ping, string>(0, new Fixed([early, slot]), out _);
        IPipelineBehavior<S.Ping, string>[]? second = state.Behaviors<S.Ping, string>(0, new Fixed([early, early, slot]), out _);

        first!.Length.ShouldBe(2);
        second!.Length.ShouldBe(3);
        second.ShouldNotContain(behavior => behavior is global::Relevox.Internal.BuiltInSlot<S.Ping, string>);
    }

    /// <summary>A container that hands out one fixed list of behaviours and no processors.</summary>
    private sealed class Fixed(IPipelineBehavior<S.Ping, string>[] behaviors) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IEnumerable<IPipelineBehavior<S.Ping, string>>) ? behaviors
            : serviceType == typeof(IEnumerable<global::Relevox.Pipeline.IRequestPostProcessor<S.Ping, string>>) ? Array.Empty<global::Relevox.Pipeline.IRequestPostProcessor<S.Ping, string>>()
            : null;
    }

    [Theory]
    [InlineData(S.Profile.Plain)]
    [InlineData(S.Profile.Pipeline)]
    public async Task Stack_traces_show_handlers_and_behaviours_but_not_the_plumbing_between_them(S.Profile profile)
    {
        // What a developer reads first when something fails. The generated mediator and Relevox.Internal are hidden
        // from stack traces and stepped through by the debugger; handlers, behaviours and processors are not.
        await using ServiceProvider provider = S.Library.Build(profile: profile);
        using IServiceScope scope = provider.CreateScope();
        ISender sender = scope.ServiceProvider.GetRequiredService<ISender>();
        RelevoxMediator typed = scope.ServiceProvider.GetRequiredService<RelevoxMediator>();

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
            await StackOf(() => sender.Send(new S.Fizzle())),
        ];

        traces[0].ShouldContain("ExplodeHandler.Handle");
        traces[3].ShouldContain("BoomHandler.Handle");
        traces[4].ShouldContain("SputterHandler.Handle");
        traces[5].ShouldContain("FizzleHandler.Handle");
        foreach (string trace in traces)
        {
            trace.ShouldNotContain("Relevox.Internal");
        }

        // The typed, interface and stream paths show nothing of the mediator. Send(object), the default publisher
        // and a request with no response through behaviours keep one frame each: they are async methods, and .NET
        // checks StackTraceHidden on the state machine the compiler generates, which no attribute can reach.
        foreach (int path in new[] { 0, 1, 4 })
        {
            traces[path].ShouldNotContain("RelevoxMediator");
        }

        foreach (int path in new[] { 2, 3 })
        {
            MediatorFrames(traces[path]).ShouldBe(1, traces[path]);
        }

        MediatorFrames(traces[5]).ShouldBe(profile == S.Profile.Pipeline ? 1 : 0, traces[5]);

        if (profile == S.Profile.Pipeline)
        {
            traces[0].ShouldContain("OuterBehavior");
        }
    }

    private static int MediatorFrames(string trace) =>
        trace.Split(Environment.NewLine).Count(line => line.Contains("RelevoxMediator", StringComparison.Ordinal));

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

    [Fact]
    public async Task A_handler_taken_through_its_interface_is_the_instance_the_mediator_uses()
    {
        await using ServiceProvider provider = Build(options => options.HandlerLifetime = ServiceLifetime.Singleton);

        provider.GetRequiredService<IRequestHandler<S.Tally, int>>()
            .ShouldBeSameAs(provider.GetRequiredService<S.TallyHandler>());
    }

    [Fact]
    public async Task A_keyed_registration_does_not_hide_the_unkeyed_interface()
    {
        await using ServiceProvider provider = Build(before: services =>
            services.AddKeyedTransient<IRequestHandler<S.Tally, int>, S.TallyHandler>("keyed"));

        provider.GetService<IRequestHandler<S.Tally, int>>().ShouldBeOfType<S.TallyHandler>();
    }

    [Fact]
    public void A_scoped_handler_taken_through_its_interface_by_a_singleton_fails_validation()
    {
        // The interface registration carries the class's lifetime, so the container catches the captive
        // dependency when it is built rather than on the first resolve.
        Should.Throw<AggregateException>(() => Build(
            options => options.HandlerLifetime = ServiceLifetime.Scoped,
            services => services.AddSingleton<TallyUser>()));
    }

    private sealed class TallyUser(IRequestHandler<S.Tally, int> tally)
    {
        public IRequestHandler<S.Tally, int> Tally { get; } = tally;
    }

    private static ServiceProvider Build(Action<global::Microsoft.Extensions.DependencyInjection.RelevoxOptions>? configure = null, Action<IServiceCollection>? before = null)
    {
        ServiceCollection services = new();
        services.AddSingleton<S.Trace>();
        before?.Invoke(services);
        Registration.AddRelevox(services, configure);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }
}
