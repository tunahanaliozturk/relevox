using Microsoft.Extensions.DependencyInjection;

namespace BridgeTests;

/// <summary>Relevox.MediatRBridge (#130, ADR 0022): MediatR's interfaces over a half-moved application.</summary>
public sealed class BridgeTests
{
    [Fact]
    public async Task A_request_goes_to_the_library_whose_interface_it_implements()
    {
        await using ServiceProvider provider = Build();
        MediatR.ISender sender = provider.GetRequiredService<MediatR.ISender>();

        (await sender.Send(new Legacy("a"))).ShouldBe("mediatr:a");
        (await sender.Send(new Moved("b"))).ShouldBe("relevox:b");

        Events(provider).ShouldBe(["MediatR handler", "Relevox handler"]);
    }

    [Fact]
    public async Task A_request_with_no_response_goes_to_its_library_with_the_token()
    {
        await using ServiceProvider provider = Build();
        MediatR.IMediator mediator = provider.GetRequiredService<MediatR.IMediator>();
        using CancellationTokenSource cancellation = new();

        await mediator.Send(new LegacyCommand());
        await mediator.Send(new MovedCommand(), cancellation.Token);

        Events(provider).ShouldBe(["MediatR command", "Relevox command(canBeCanceled=True)"]);
    }

    [Fact]
    public async Task An_untyped_send_routes_by_runtime_type_and_answers_with_MediatR_s_Unit()
    {
        await using ServiceProvider provider = Build();
        MediatR.ISender sender = provider.GetRequiredService<MediatR.ISender>();

        (await sender.Send((object)new Moved("c"))).ShouldBe("relevox:c");
        (await sender.Send((object)new Legacy("d"))).ShouldBe("mediatr:d");
        (await sender.Send((object)new MovedCommand())).ShouldBeOfType<MediatR.Unit>();
        (await sender.Send((object)new LegacyCommand())).ShouldBeOfType<MediatR.Unit>();
    }

    [Fact]
    public async Task Streams_go_to_their_library_typed_and_untyped()
    {
        await using ServiceProvider provider = Build();
        MediatR.ISender sender = provider.GetRequiredService<MediatR.ISender>();

        (await sender.CreateStream(new MovedCount(3)).ToArrayAsync()).ShouldBe([1, 2, 3]);
        (await sender.CreateStream(new LegacyCount(2)).ToArrayAsync()).ShouldBe([-1, -2]);
        (await sender.CreateStream((object)new MovedCount(2)).ToArrayAsync()).ShouldBe([1, 2]);
        (await sender.CreateStream((object)new LegacyCount(1)).ToArrayAsync()).ShouldBe([-1]);
    }

    [Fact]
    public async Task A_notification_reaches_the_handlers_on_its_side()
    {
        await using ServiceProvider provider = Build();
        MediatR.IPublisher publisher = provider.GetRequiredService<MediatR.IPublisher>();

        await publisher.Publish(new LegacyEvent());
        await publisher.Publish(new MovedEvent());
        await publisher.Publish((object)new MovedEvent());

        Events(provider).ShouldBe(["MediatR LegacyEvent", "Relevox MovedEvent", "Relevox MovedEvent"]);
    }

    [Fact]
    public async Task A_notification_with_handlers_on_both_sides_runs_MediatR_s_first_then_Relevox_s()
    {
        await using ServiceProvider provider = Build();
        MediatR.IPublisher publisher = provider.GetRequiredService<MediatR.IPublisher>();

        await publisher.Publish(new SharedEvent());
        await publisher.Publish((object)new SharedEvent());

        Events(provider).ShouldBe(["MediatR SharedEvent", "Relevox SharedEvent", "MediatR SharedEvent", "Relevox SharedEvent"]);
    }

    [Fact]
    public async Task A_MediatR_handler_that_throws_stops_the_Relevox_side_as_it_would_stop_the_next_MediatR_handler()
    {
        await using ServiceProvider provider = Build();

        await Should.ThrowAsync<InvalidOperationException>(
            () => provider.GetRequiredService<MediatR.IPublisher>().Publish(new SharedEvent(Fail: true)));

        Events(provider).ShouldBe(["MediatR SharedEvent"]);
    }

    [Fact]
    public async Task A_behaviour_registered_with_both_libraries_runs_once_per_request()
    {
        await using ServiceProvider provider = Build(behaviours: true);
        MediatR.ISender sender = provider.GetRequiredService<MediatR.ISender>();

        await sender.Send(new Legacy("a"));
        await sender.Send(new Moved("b"));

        Events(provider).ShouldBe(["MediatR behaviour Legacy", "MediatR handler", "Relevox behaviour Moved", "Relevox handler"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_bridge_takes_MediatR_s_interfaces_whichever_is_registered_first(bool mediatrFirst)
    {
        ServiceCollection services = Services(mediatrFirst);
        await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        Type bridge = provider.GetRequiredService<MediatR.IMediator>().GetType();
        bridge.Name.ShouldBe("BridgedMediator");
        provider.GetRequiredService<MediatR.ISender>().GetType().ShouldBe(bridge);
        provider.GetRequiredService<MediatR.IPublisher>().GetType().ShouldBe(bridge);
        services.Count(static descriptor => descriptor.ServiceType == typeof(MediatR.IMediator) && !descriptor.IsKeyedService).ShouldBe(1);
        (await provider.GetRequiredService<MediatR.ISender>().Send(new Legacy("e"))).ShouldBe("mediatr:e");
    }

    [Fact]
    public async Task Calling_it_twice_changes_nothing()
    {
        ServiceCollection services = Services(mediatrFirst: true);
        int count = services.Count;

        services.AddMediatRBridge();
        await using ServiceProvider provider = services.BuildServiceProvider();

        services.Count.ShouldBe(count);
        (await provider.GetRequiredService<MediatR.ISender>().Send(new Moved("f"))).ShouldBe("relevox:f");
    }

    [Fact]
    public async Task MediatR_s_own_mediator_type_is_kept_for_the_messages_that_have_not_moved()
    {
        ServiceCollection services = [];
        services.AddSingleton<Trace>();
        services.AddMediatR(static options =>
        {
            options.RegisterServicesFromAssemblyContaining<BridgeTests>();
            options.MediatorImplementationType = typeof(CountingMediator);
        });
        services.AddRelevox();
        services.AddMediatRBridge();
        await using ServiceProvider provider = services.BuildServiceProvider();

        await provider.GetRequiredService<MediatR.ISender>().Send(new Legacy("g"));
        await provider.GetRequiredService<MediatR.ISender>().Send(new Moved("h"));

        Events(provider).ShouldBe(["CountingMediator", "MediatR handler", "Relevox handler"]);
    }

    private static ServiceProvider Build(bool behaviours = false) =>
        Services(mediatrFirst: true, behaviours).BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

    private static ServiceCollection Services(bool mediatrFirst, bool behaviours = false)
    {
        ServiceCollection services = [];
        services.AddSingleton<Trace>();
        if (!mediatrFirst)
        {
            services.AddMediatRBridge();
        }

        services.AddMediatR(options =>
        {
            options.RegisterServicesFromAssemblyContaining<BridgeTests>();
            if (behaviours)
            {
                options.AddOpenBehavior(typeof(Logged<,>));
            }
        });
        services.AddRelevox(options =>
        {
            if (behaviours)
            {
                options.AddOpenBehavior(typeof(Logged<,>));
            }
        });
        if (mediatrFirst)
        {
            services.AddMediatRBridge();
        }

        return services;
    }

    private static List<string> Events(ServiceProvider provider) => provider.GetRequiredService<Trace>().Events;
}

/// <summary>A mediator of the application's own, as <c>MediatorImplementationType</c> allows.</summary>
public sealed class CountingMediator(IServiceProvider services, Trace trace) : MediatR.IMediator
{
    private readonly MediatR.Mediator _inner = new(services);

    public Task<TResponse> Send<TResponse>(MediatR.IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        trace.Events.Add(nameof(CountingMediator));
        return _inner.Send(request, cancellationToken);
    }

    public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
        where TRequest : MediatR.IRequest => _inner.Send(request, cancellationToken);

    public Task<object?> Send(object request, CancellationToken cancellationToken = default) => _inner.Send(request, cancellationToken);

    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(MediatR.IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
        _inner.CreateStream(request, cancellationToken);

    public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => _inner.CreateStream(request, cancellationToken);

    public Task Publish(object notification, CancellationToken cancellationToken = default) => _inner.Publish(notification, cancellationToken);

    public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : MediatR.INotification => _inner.Publish(notification, cancellationToken);
}
