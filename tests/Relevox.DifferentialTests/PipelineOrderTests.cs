extern alias onrelevox;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Registration = onrelevox::Microsoft.Extensions.DependencyInjection.RelevoxServiceCollectionExtensions;
using S = onrelevox::Scenarios;

namespace Relevox.DifferentialTests;

/// <summary>
/// <see cref="PipelineOrderAttribute"/> (#97), which MediatR has no equivalent for. Without the attribute the
/// differential tests are the proof that nothing moved.
/// </summary>
public sealed class PipelineOrderTests
{
    [Fact]
    public async Task Ordered_behaviours_registered_in_reverse_run_in_attribute_order()
    {
        await using ServiceProvider provider = Build(before: services =>
        {
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(Inner<,>));
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(Outer<,>));
        });

        // The first send learns the order and the second uses what it learned.
        (await Send(provider)).ShouldBe(["Outer>", "Inner>", "PingHandler(x)", "Inner<", "Outer<"]);
        (await Send(provider)).ShouldBe(["Outer>", "Inner>", "PingHandler(x)", "Inner<", "Outer<"]);
    }

    [Fact]
    public async Task Behaviours_without_the_attribute_count_as_zero_and_keep_their_registration_order()
    {
        await using ServiceProvider provider = Build(before: services =>
        {
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(Inner<,>));
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(S.LateBehavior<,>));
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(Outer<,>));
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(S.EarlyBehavior<,>));
        });

        (await Send(provider)).ShouldBe(["Outer>", "Late>", "Early>", "Inner>", "PingHandler(x)", "Inner<", "Early<", "Late<", "Outer<"]);
    }

    [Fact]
    public async Task A_built_in_behaviour_counts_as_zero()
    {
        // Registered before AddRelevox, the inner behaviour would run outside the post-processor; its order puts
        // it inside, so the post-processor sees the response after it.
        await using ServiceProvider provider = Build(
            options => options.AddRequestPostProcessor<global::Relevox.Pipeline.IRequestPostProcessor<S.Ping, string>, S.PingPost>(),
            before: services => services.AddTransient(typeof(IPipelineBehavior<,>), typeof(Inner<,>)));

        (await Send(provider)).ShouldBe(["Inner>", "PingHandler(x)", "Inner<", "PingPost(x pong)"]);
        (await Send(provider)).ShouldBe(["Inner>", "PingHandler(x)", "Inner<", "PingPost(x pong)"]);
    }

    [Fact]
    public async Task An_idle_built_in_is_judged_where_the_order_puts_it()
    {
        // The pre-processor slot has nothing to run for Flaky. Registered first, the catching behaviour would sit
        // outside it and see the async pass-through's faulted task; its order puts it inside, next to the handler,
        // where it sees the handler's exception itself, as it would in MediatR in that position.
        await using ServiceProvider provider = Build(
            options => options.AddOpenRequestPreProcessor(typeof(NoPing<>)),
            before: services => services.AddTransient(typeof(IPipelineBehavior<,>), typeof(Catching<,>)));
        S.Trace trace = provider.GetRequiredService<S.Trace>();
        ISender sender = provider.GetRequiredService<ISender>();

        await Should.ThrowAsync<TimeoutException>(() => sender.Send(new S.Flaky()));
        (await sender.Send(new S.Flaky())).ShouldBe(2);

        trace.Events.ShouldBe(["FlakyHandler", "exception", "FlakyHandler"]);
    }

    [Fact]
    public async Task Closed_behaviours_are_ordered_too()
    {
        await using ServiceProvider provider = Build(before: services =>
        {
            services.AddTransient<IPipelineBehavior<S.Ping, string>, ClosedInner>();
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(Outer<,>));
        });

        (await Send(provider)).ShouldBe(["Outer>", "ClosedInner>", "PingHandler(x)", "ClosedInner<", "Outer<"]);
    }

    [Fact]
    public async Task The_attribute_is_inherited()
    {
        await using ServiceProvider provider = Build(before: services =>
        {
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(DerivedInner<,>));
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(S.EarlyBehavior<,>));
        });

        (await Send(provider)).ShouldBe(["Early>", "DerivedInner>", "PingHandler(x)", "DerivedInner<", "Early<"]);
    }

    [Fact]
    public async Task Singleton_behaviours_are_ordered_on_every_send_without_reordering_the_containers_array()
    {
        // With every behaviour a singleton the container can hand out one cached array on every call, so ordering
        // it in place would scramble it on the next send.
        await using ServiceProvider provider = Build(before: services =>
        {
            services.AddSingleton(typeof(IPipelineBehavior<,>), typeof(Inner<,>));
            services.AddSingleton(typeof(IPipelineBehavior<,>), typeof(Outer<,>));
        });

        for (int round = 0; round < 3; round++)
        {
            (await Send(provider)).ShouldBe(["Outer>", "Inner>", "PingHandler(x)", "Inner<", "Outer<"]);
        }

        IEnumerable<IPipelineBehavior<S.Ping, string>> cached = provider.GetServices<IPipelineBehavior<S.Ping, string>>();
        cached.ShouldBeSameAs(provider.GetServices<IPipelineBehavior<S.Ping, string>>());
        cached.Take(2).Select(static behavior => behavior.GetType().Name).ShouldBe(["Inner`2", "Outer`2"]);
    }

    [Fact]
    public async Task Stream_behaviours_are_ordered()
    {
        await using ServiceProvider provider = Build(before: services =>
        {
            services.AddTransient(typeof(IStreamPipelineBehavior<,>), typeof(InnerStream<,>));
            services.AddTransient(typeof(IStreamPipelineBehavior<,>), typeof(OuterStream<,>));
        });

        S.Trace trace = provider.GetRequiredService<S.Trace>();
        ISender sender = provider.GetRequiredService<ISender>();
        for (int round = 0; round < 2; round++)
        {
            await foreach (int item in sender.CreateStream(new S.Countdown(1)))
            {
                trace.Add($"item {item}");
            }
        }

        trace.Events.Where(static entry => entry.EndsWith("Stream", StringComparison.Ordinal)).ShouldBe(["OuterStream", "InnerStream", "OuterStream", "InnerStream"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_scope_that_returns_other_behaviours_is_ordered_too(bool firstInPlace)
    {
        // Containers such as Autofac let a child scope register behaviours of its own, so one request type can come
        // back with another list than the one its first send learned from, already in order or not.
        global::Relevox.Internal.PipelineState<PipelineOrderTests> state = new(1, 0, builtInsForEveryRequest: false);
        S.Trace trace = new();
        IPipelineBehavior<S.Ping, string> inner = new Inner<S.Ping, string>(trace);
        IPipelineBehavior<S.Ping, string> outer = new Outer<S.Ping, string>(trace);
        IPipelineBehavior<S.Ping, string> early = new S.EarlyBehavior<S.Ping, string>(trace);

        IPipelineBehavior<S.Ping, string>[]? first = state.Behaviors<S.Ping, string>(0, new Fixed(firstInPlace ? [outer, inner] : [inner, outer]), out _);
        IPipelineBehavior<S.Ping, string>[]? swapped = state.Behaviors<S.Ping, string>(0, new Fixed(firstInPlace ? [inner, outer] : [outer, inner]), out _);
        IPipelineBehavior<S.Ping, string>[]? other = state.Behaviors<S.Ping, string>(0, new Fixed([inner, early]), out _);

        first.ShouldBe([outer, inner]);
        swapped.ShouldBe([outer, inner]);
        other.ShouldBe([early, inner]);
    }

    [Fact]
    public void A_scope_that_adds_an_ordered_behaviour_to_a_list_learned_without_one_is_ordered()
    {
        global::Relevox.Internal.PipelineState<PipelineOrderTests> state = new(1, 0, builtInsForEveryRequest: false);
        S.Trace trace = new();
        IPipelineBehavior<S.Ping, string> outer = new Outer<S.Ping, string>(trace);
        IPipelineBehavior<S.Ping, string> early = new S.EarlyBehavior<S.Ping, string>(trace);
        IPipelineBehavior<S.Ping, string>[] plain = [early];

        state.Behaviors<S.Ping, string>(0, new Fixed(plain), out _).ShouldBeSameAs(plain);
        state.Behaviors<S.Ping, string>(0, new Fixed([early, outer]), out _).ShouldBe([outer, early]);
        state.Behaviors<S.Ping, string>(0, new Fixed(plain), out _).ShouldBeSameAs(plain);
    }

    [Fact]
    public void Ordered_behaviours_already_in_place_run_from_the_containers_array()
    {
        global::Relevox.Internal.PipelineState<PipelineOrderTests> state = new(1, 0, builtInsForEveryRequest: false);
        S.Trace trace = new();
        IPipelineBehavior<S.Ping, string>[] inPlace = [new Outer<S.Ping, string>(trace), new Inner<S.Ping, string>(trace)];

        state.Behaviors<S.Ping, string>(0, new Fixed(inPlace), out _).ShouldBeSameAs(inPlace);
        state.Behaviors<S.Ping, string>(0, new Fixed(inPlace), out _).ShouldBeSameAs(inPlace);
    }

    [Fact]
    public void A_stream_scope_that_returns_other_behaviours_is_ordered_too()
    {
        global::Relevox.Internal.PipelineState<PipelineOrderTests> state = new(0, 1, builtInsForEveryRequest: false);
        S.Trace trace = new();
        IStreamPipelineBehavior<S.Countdown, int> inner = new InnerStream<S.Countdown, int>(trace);
        IStreamPipelineBehavior<S.Countdown, int> outer = new OuterStream<S.Countdown, int>(trace);

        state.StreamBehaviors<S.Countdown, int>(0, new FixedStream([outer, inner])).ShouldBe([outer, inner]);
        state.StreamBehaviors<S.Countdown, int>(0, new FixedStream([inner, outer])).ShouldBe([outer, inner]);
        state.StreamBehaviors<S.Countdown, int>(0, new FixedStream([inner])).ShouldBe([inner]);
        state.StreamBehaviors<S.Countdown, int>(0, new FixedStream([inner, outer])).ShouldBe([outer, inner]);
    }

    private static async Task<IReadOnlyList<string>> Send(ServiceProvider provider)
    {
        S.Trace trace = provider.GetRequiredService<S.Trace>();
        int before = trace.Events.Count;
        await provider.GetRequiredService<ISender>().Send(new S.Ping("x"));
        return [.. trace.Events.Skip(before)];
    }

    private static ServiceProvider Build(
        Action<global::Microsoft.Extensions.DependencyInjection.RelevoxOptions>? configure = null,
        Action<IServiceCollection>? before = null,
        Action<IServiceCollection>? after = null)
    {
        ServiceCollection services = new();
        services.AddSingleton<S.Trace>();
        before?.Invoke(services);
        Registration.AddRelevox(services, configure);
        after?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    /// <summary>A container that hands out one fixed list of behaviours.</summary>
    private sealed class Fixed(IPipelineBehavior<S.Ping, string>[] behaviors) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IEnumerable<IPipelineBehavior<S.Ping, string>>) ? behaviors : null;
    }

    /// <summary>A container that hands out one fixed list of stream behaviours.</summary>
    private sealed class FixedStream(IStreamPipelineBehavior<S.Countdown, int>[] behaviors) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IEnumerable<IStreamPipelineBehavior<S.Countdown, int>>) ? behaviors : null;
    }

    private abstract class Step<TRequest, TResponse>(S.Trace trace, string name) : IPipelineBehavior<TRequest, TResponse>
        where TRequest : notnull
    {
        public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        {
            trace.Add(name + ">");
            TResponse response = await next(cancellationToken);
            trace.Add(name + "<");
            return response;
        }
    }

    [PipelineOrder(-10)]
    private sealed class Outer<TRequest, TResponse>(S.Trace trace) : Step<TRequest, TResponse>(trace, "Outer")
        where TRequest : notnull;

    [PipelineOrder(10)]
    private sealed class Inner<TRequest, TResponse>(S.Trace trace) : Step<TRequest, TResponse>(trace, "Inner")
        where TRequest : notnull;

    [PipelineOrder(10)]
    private sealed class ClosedInner(S.Trace trace) : Step<S.Ping, string>(trace, "ClosedInner");

    [PipelineOrder(10)]
    private abstract class OrderedBase<TRequest, TResponse>(S.Trace trace, string name) : Step<TRequest, TResponse>(trace, name)
        where TRequest : notnull;

    private sealed class DerivedInner<TRequest, TResponse>(S.Trace trace) : OrderedBase<TRequest, TResponse>(trace, "DerivedInner")
        where TRequest : notnull;

    /// <summary>Not async, so it sees whatever the inside does: an exception, or a faulted task.</summary>
    [PipelineOrder(10)]
    private sealed class Catching<TRequest, TResponse>(S.Trace trace) : IPipelineBehavior<TRequest, TResponse>
        where TRequest : notnull
    {
        public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        {
            Task<TResponse> task;
            try
            {
                task = next(cancellationToken);
            }
            catch (TimeoutException)
            {
                trace.Add("exception");
                throw;
            }

            if (task.IsFaulted)
            {
                trace.Add("faulted task");
            }

            return task;
        }
    }

    /// <summary>A pre-processor for a request type nothing sends, so the slot is idle for Ping.</summary>
    private sealed class NoPing<TRequest> : global::Relevox.Pipeline.IRequestPreProcessor<TRequest>
        where TRequest : S.IAudited
    {
        public Task Process(TRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private abstract class StreamStep<TRequest, TResponse>(S.Trace trace, string name) : IStreamPipelineBehavior<TRequest, TResponse>
        where TRequest : notnull
    {
        public async IAsyncEnumerable<TResponse> Handle(TRequest request, StreamHandlerDelegate<TResponse> next, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            trace.Add(name);
            await foreach (TResponse item in next().WithCancellation(cancellationToken))
            {
                yield return item;
            }
        }
    }

    [PipelineOrder(-1)]
    private sealed class OuterStream<TRequest, TResponse>(S.Trace trace) : StreamStep<TRequest, TResponse>(trace, "OuterStream")
        where TRequest : notnull;

    [PipelineOrder(1)]
    private sealed class InnerStream<TRequest, TResponse>(S.Trace trace) : StreamStep<TRequest, TResponse>(trace, "InnerStream")
        where TRequest : notnull;
}
