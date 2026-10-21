extern alias onrelevox;
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Registration = onrelevox::Microsoft.Extensions.DependencyInjection.RelevoxServiceCollectionExtensions;
using S = onrelevox::Scenarios;

namespace Relevox.DifferentialTests;

/// <summary>
/// Activity listeners are process-wide, so the telemetry tests run on their own, after everything else: a
/// listener left by one would otherwise turn on spans in another, and the test that nothing is created
/// without a listener would mean nothing.
/// </summary>
[CollectionDefinition(nameof(RunsAlone), DisableParallelization = true)]
public sealed class RunsAlone;

/// <summary>
/// The spans the generated mediator emits with <c>options.Telemetry.Enabled</c>, named as ADR 0013 decides, and
/// proof that turning them on changes nothing a caller can observe.
/// </summary>
[Collection(nameof(RunsAlone))]
public sealed class TelemetryTests
{
    public static TheoryData<string> ScenarioNames() => DifferentialTests.ScenarioNames();

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public async Task With_telemetry_on_Relevox_still_behaves_like_MediatR(string scenario)
    {
        using Spans spans = new();
        using Measurements measurements = new();
        string mediatr = await DifferentialTests.Invoke(DifferentialTests.OnMediatR, scenario);

        S.Library.TelemetryOn.Value = true;
        string relevox = await DifferentialTests.Invoke(DifferentialTests.OnRelevox, scenario);

        relevox.ShouldBe(mediatr, $"MediatR: {mediatr}{Environment.NewLine}Relevox:   {relevox}");
    }

    [Fact]
    public async Task The_scenarios_really_run_traced_when_telemetry_is_on()
    {
        // Agreement above could otherwise mean the switch never reached the mediator.
        using Spans spans = new();
        S.Library.TelemetryOn.Value = true;

        await DifferentialTests.Invoke(DifferentialTests.OnRelevox, "SendThroughSender");

        spans.Recorded.ShouldContain(span => span.OperationName == "send Ping");
    }

    [Fact]
    public async Task A_send_is_one_span_named_after_the_request_and_tagged_with_its_handler()
    {
        using Spans spans = new();
        await using ServiceProvider provider = Build();

        (await provider.GetRequiredService<ISender>().Send(new S.Ping("x"))).ShouldBe("x pong");

        Activity span = spans.Recorded.ShouldHaveSingleItem();
        span.OperationName.ShouldBe("send Ping");
        span.Kind.ShouldBe(ActivityKind.Internal);
        span.Status.ShouldBe(ActivityStatusCode.Unset);
        span.GetTagItem("relevox.operation").ShouldBe("send");
        span.GetTagItem("relevox.message.type").ShouldBe("Scenarios.Ping");
        span.GetTagItem("relevox.handler.type").ShouldBe("Scenarios.PingHandler");
        span.GetTagItem("error.type").ShouldBeNull();
    }

    [Fact]
    public async Task A_send_from_inside_a_handler_is_a_child_span_and_the_caller_keeps_its_own_current_activity()
    {
        using Spans spans = new();
        await using ServiceProvider provider = Build();
        ISender sender = provider.GetRequiredService<ISender>();

        using Activity outer = Spans.Caller.StartActivity("caller")!;

        // The handler yields first, so Send returns a task that is still running.
        Task<string> pending = sender.Send(new S.Relay("deep"));
        Activity.Current.ShouldBeSameAs(outer);
        (await pending).ShouldBe("deep pong");
        Activity.Current.ShouldBeSameAs(outer);

        Activity relay = spans.Named("send Relay");
        Activity ping = spans.Named("send Ping");
        relay.ParentSpanId.ShouldBe(outer.SpanId);
        ping.ParentSpanId.ShouldBe(relay.SpanId);
        ping.TraceId.ShouldBe(outer.TraceId);
    }

    [Fact]
    public async Task A_failed_send_marks_its_span_and_rethrows_the_same_exception()
    {
        using Spans spans = new();
        await using ServiceProvider provider = Build();
        ISender sender = provider.GetRequiredService<ISender>();

        // One handler throws before returning a task, the other faults the task after a yield.
        Exception thrown = await Caught(() => sender.Send(new S.Explode()));
        Exception faulted = await Caught(() => sender.Send(new S.Stall()));

        thrown.ShouldBeOfType<NotSupportedException>().Message.ShouldBe("thrown before any await");
        faulted.ShouldBeOfType<TimeoutException>();
        ShouldBeFailed(spans.Named("send Explode"), thrown);
        ShouldBeFailed(spans.Named("send Stall"), faulted);
    }

    [Fact]
    public async Task A_publish_is_one_span_with_the_number_of_handlers_it_reaches()
    {
        using Spans spans = new();
        await using ServiceProvider provider = Build();

        await provider.GetRequiredService<IPublisher>().Publish(new S.Alarm("north"));

        Activity span = spans.Recorded.ShouldHaveSingleItem();
        span.OperationName.ShouldBe("publish Alarm");
        span.GetTagItem("relevox.operation").ShouldBe("publish");
        span.GetTagItem("relevox.message.type").ShouldBe("Scenarios.Alarm");
        span.GetTagItem("relevox.notification.handler_count").ShouldBe(provider.GetRequiredService<S.Trace>().Events.Count);
        span.GetTagItem("relevox.handler.type").ShouldBeNull();
    }

    [Fact]
    public async Task A_failed_publish_marks_its_span()
    {
        using Spans spans = new();
        await using ServiceProvider provider = Build();

        Exception thrown = await Caught(() => provider.GetRequiredService<IPublisher>().Publish(new S.Boom()));

        ShouldBeFailed(spans.Named("publish Boom"), thrown);
    }

    [Fact]
    public async Task A_notification_type_known_only_at_run_time_is_named_from_its_type_as_the_generator_names_one()
    {
        using Spans spans = new();
        await using ServiceProvider provider = Build();
        IPublisher publisher = provider.GetRequiredService<IPublisher>();

        // Rerouted<string> has a handler the build saw, so its names are constants in the generated code.
        // Rerouted<int> is built from a type parameter and reaches only generic handlers closed at run time.
        await publisher.Publish(new S.Rerouted<string>("Ankara"));
        int before = provider.GetRequiredService<S.Trace>().Events.Count;
        await publisher.Publish(S.Shipments.Reroute(42));
        await publisher.Publish((object)S.Shipments.Reroute(43));
        int reached = (provider.GetRequiredService<S.Trace>().Events.Count - before) / 2;

        Activity known = spans.Named("publish Rerouted<String>");
        known.GetTagItem("relevox.message.type").ShouldBe("Scenarios.Rerouted<System.String>");

        Activity[] unknown = [.. spans.Recorded.Where(span => span.OperationName == "publish Rerouted<Int32>")];
        unknown.Length.ShouldBe(2);
        reached.ShouldBe(2);
        foreach (Activity span in unknown)
        {
            span.GetTagItem("relevox.message.type").ShouldBe("Scenarios.Rerouted<System.Int32>");
            span.GetTagItem("relevox.notification.handler_count").ShouldBe(reached);
        }
    }

    [Fact]
    public async Task A_stream_gets_one_span_per_enumeration_and_none_when_never_enumerated()
    {
        using Spans spans = new();
        await using ServiceProvider provider = Build();
        ISender sender = provider.GetRequiredService<ISender>();

        IAsyncEnumerable<int> stream = sender.CreateStream(new S.Countdown(3));
        _ = sender.CreateStream(new S.Countdown(9));
        (await stream.ToListAsync()).ShouldBe([3, 2, 1]);
        (await stream.ToListAsync()).ShouldBe([3, 2, 1]);

        spans.Recorded.Count.ShouldBe(2);
        foreach (Activity span in spans.Recorded)
        {
            span.OperationName.ShouldBe("stream Countdown");
            span.GetTagItem("relevox.operation").ShouldBe("stream");
            span.GetTagItem("relevox.message.type").ShouldBe("Scenarios.Countdown");
            span.GetTagItem("relevox.handler.type").ShouldBe("Scenarios.CountdownHandler");
            span.Status.ShouldBe(ActivityStatusCode.Unset);
        }
    }

    [Fact]
    public async Task A_stream_span_ends_on_a_failure_and_on_an_early_dispose()
    {
        using Spans spans = new();
        await using ServiceProvider provider = Build();
        ISender sender = provider.GetRequiredService<ISender>();

        Exception thrown = await Caught(async () =>
        {
            await foreach (int item in sender.CreateStream(new S.Sputter()))
            {
                item.ShouldBe(1);
            }
        });
        await foreach (int item in sender.CreateStream(new S.Countdown(5)))
        {
            item.ShouldBe(5);
            break;
        }

        ShouldBeFailed(spans.Named("stream Sputter"), thrown);
        spans.Named("stream Countdown").Status.ShouldBe(ActivityStatusCode.Unset);
    }

    [Fact]
    public async Task A_send_from_a_stream_disposed_early_is_a_child_of_the_stream_span()
    {
        using Spans spans = new();
        await using ServiceProvider provider = Build();

        await foreach (int item in provider.GetRequiredService<ISender>().CreateStream(new S.Lingering()))
        {
            item.ShouldBe(1);
            break;
        }

        spans.Named("send Tally").ParentSpanId.ShouldBe(spans.Named("stream Lingering").SpanId);
    }

    [Fact]
    public async Task Nothing_is_created_when_nothing_listens_or_when_telemetry_is_off()
    {
        await using (ServiceProvider on = Build())
        {
            (await on.GetRequiredService<ISender>().Send(new S.Probe())).ShouldBe("none");
        }

        using Spans spans = new();
        await using (ServiceProvider off = Build(telemetry: false))
        {
            (await off.GetRequiredService<ISender>().Send(new S.Probe())).ShouldBe("none");
            await off.GetRequiredService<IPublisher>().Publish(new S.Alarm("east"));
            await off.GetRequiredService<ISender>().CreateStream(new S.Countdown(2)).ToListAsync();
        }

        spans.Recorded.ShouldBeEmpty();

        await using ServiceProvider listened = Build();
        (await listened.GetRequiredService<ISender>().Send(new S.Probe())).ShouldBe("send Probe");
    }

    internal static void ShouldBeFailed(Activity span, Exception exception)
    {
        span.Status.ShouldBe(ActivityStatusCode.Error);
        span.GetTagItem("error.type").ShouldBe(exception.GetType().FullName);
        ActivityEvent recorded = span.Events.ShouldHaveSingleItem();
        recorded.Name.ShouldBe("exception");
        recorded.Tags.ShouldContain(new KeyValuePair<string, object?>("exception.type", exception.GetType().FullName));
    }

    internal static async Task<Exception> Caught(Func<Task> call)
    {
        try
        {
            await call();
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new ShouldAssertException("Expected an exception.");
    }

    internal static ServiceProvider Build(bool telemetry = true, Action<RelevoxOptions>? configure = null)
    {
        ServiceCollection services = new();
        services.AddSingleton<S.Trace>();
        Registration.AddRelevox(services, options =>
        {
            options.Telemetry.Enabled = telemetry;
            configure?.Invoke(options);
        });
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    /// <summary>Records every Relevox span that ends while it is alive, as an in-memory exporter would.</summary>
    internal sealed class Spans : IDisposable
    {
        /// <summary>Stands in for the application's own spans, such as an ASP.NET Core request.</summary>
        public static readonly ActivitySource Caller = new(CallerName);

        private const string CallerName = "Relevox.Tests.Caller";

        private readonly ConcurrentQueue<Activity> _recorded = new();
        private readonly ActivityListener _listener;

        public Spans()
        {
            _listener = new ActivityListener
            {
                // By name, not by reference to Caller: the static field can be initialised while this listener is
                // being added, and a comparison against it then misses the source for good.
                ShouldListenTo = static source => source.Name == RelevoxTelemetry.SourceName || source.Name == CallerName,
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    if (activity.Source.Name == RelevoxTelemetry.SourceName)
                    {
                        _recorded.Enqueue(activity);
                    }
                },
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public IReadOnlyList<Activity> Recorded => [.. _recorded];

        public Activity Named(string name) => Recorded.Where(span => span.OperationName == name).ShouldHaveSingleItem();

        public void Dispose() => _listener.Dispose();
    }
}
