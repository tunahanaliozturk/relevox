extern alias onrelevox;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Relevox.NotificationPublishers;
using S = onrelevox::Scenarios;
using Spans = Relevox.DifferentialTests.TelemetryTests.Spans;

namespace Relevox.DifferentialTests;

/// <summary>
/// The span per notification handler that <c>options.Telemetry.NotificationHandlerSpans</c> adds, with each
/// publisher a notification can go through, and proof that it is absent while the option is off.
/// </summary>
[Collection(nameof(RunsAlone))]
public sealed class HandlerSpanTests
{
    public static TheoryData<string> ScenarioNames() => DifferentialTests.ScenarioNames();

    public static TheoryData<string> Publishers() => ["ForeachAwait", "TaskWhenAll", "Custom"];

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public async Task With_handler_spans_on_Relevox_still_behaves_like_MediatR(string scenario)
    {
        using Spans spans = new();
        string mediatr = await DifferentialTests.Invoke(DifferentialTests.OnMediatR, scenario);

        S.Library.TelemetryOn.Value = true;
        S.Library.HandlerSpansOn.Value = true;
        string relevox = await DifferentialTests.Invoke(DifferentialTests.OnRelevox, scenario);

        relevox.ShouldBe(mediatr, $"MediatR: {mediatr}{Environment.NewLine}Relevox:   {relevox}");
    }

    [Fact]
    public async Task The_scenarios_really_run_with_handler_spans_when_the_option_is_on()
    {
        // Agreement above could otherwise mean the option never reached the mediator.
        using Spans spans = new();
        S.Library.TelemetryOn.Value = true;
        S.Library.HandlerSpansOn.Value = true;

        await DifferentialTests.Invoke(DifferentialTests.OnRelevox, "PublishToSeveralHandlers");

        spans.Recorded.ShouldContain(span => span.OperationName == "handle Alarm");
    }

    [Theory]
    [MemberData(nameof(Publishers))]
    public async Task Each_handler_gets_a_child_span_of_the_publish(string publisher)
    {
        using Spans spans = new();
        await using ServiceProvider provider = Build(publisher);

        await provider.GetRequiredService<IPublisher>().Publish(new S.Alarm("north"));

        Activity publish = spans.Named("publish Alarm");
        Activity[] handlers = [.. spans.Recorded.Where(span => span.OperationName == "handle Alarm")];
        handlers.Length.ShouldBe((int)publish.GetTagItem("relevox.notification.handler_count")!);
        handlers.Length.ShouldBe(provider.GetRequiredService<S.Trace>().Events.Count(Reached));
        handlers.Select(span => span.GetTagItem("relevox.handler.type")).Distinct().Count().ShouldBe(handlers.Length);
        handlers.Select(span => span.GetTagItem("relevox.handler.type")).ShouldContain("Scenarios.AlarmLogger");
        handlers.Select(span => span.GetTagItem("relevox.handler.type")).ShouldContain("Scenarios.SignalAuditor");
        foreach (Activity span in handlers)
        {
            span.ParentSpanId.ShouldBe(publish.SpanId);
            span.TraceId.ShouldBe(publish.TraceId);
            span.Kind.ShouldBe(ActivityKind.Internal);
            span.Status.ShouldBe(ActivityStatusCode.Unset);
            span.GetTagItem("relevox.operation").ShouldBe("handle");
            span.GetTagItem("relevox.message.type").ShouldBe("Scenarios.Alarm");
            span.GetTagItem("error.type").ShouldBeNull();
        }

        // One handler's span is not left current for the next one, or for the caller.
        Activity.Current.ShouldBeNull();
    }

    [Theory]
    [MemberData(nameof(Publishers))]
    public async Task The_failing_handler_is_the_one_whose_span_is_marked(string publisher)
    {
        using Spans spans = new();
        await using ServiceProvider provider = Build(publisher);

        Exception thrown = await TelemetryTests.Caught(() => provider.GetRequiredService<IPublisher>().Publish(new S.Brownout("east")));

        thrown.ShouldBeOfType<TimeoutException>();
        Activity[] handlers = [.. spans.Recorded.Where(span => span.OperationName == "handle Brownout")];
        TelemetryTests.ShouldBeFailed(handlers.Where(span => Equals(span.GetTagItem("relevox.handler.type"), "Scenarios.BrownoutBreaker")).ShouldHaveSingleItem(), thrown);
        foreach (Activity span in handlers.Where(span => Equals(span.GetTagItem("relevox.handler.type"), "Scenarios.BrownoutLogger")))
        {
            span.Status.ShouldBe(ActivityStatusCode.Unset);
        }

        TelemetryTests.ShouldBeFailed(spans.Named("publish Brownout"), thrown);
    }

    [Theory]
    [MemberData(nameof(Publishers))]
    public async Task A_handler_that_throws_before_returning_a_task_still_fails_as_it_did(string publisher)
    {
        using Spans spans = new();
        await using ServiceProvider provider = Build(publisher);

        Exception thrown = await TelemetryTests.Caught(() => provider.GetRequiredService<IPublisher>().Publish(new S.Boom()));

        // BoomHandler throws; the catch-all watcher also handles Boom and, depending on the publisher, runs too.
        thrown.ShouldBeOfType<NotSupportedException>();
        Activity failed = spans.Recorded
            .Where(span => span.OperationName == "handle Boom" && Equals(span.GetTagItem("relevox.handler.type"), "Scenarios.BoomHandler"))
            .ShouldHaveSingleItem();
        TelemetryTests.ShouldBeFailed(failed, thrown);
        failed.ParentSpanId.ShouldBe(spans.Named("publish Boom").SpanId);
    }

    [Theory]
    [MemberData(nameof(Publishers))]
    public async Task A_notification_type_known_only_at_run_time_gets_handler_spans_named_from_its_types(string publisher)
    {
        using Spans spans = new();
        await using ServiceProvider provider = Build(publisher);

        await provider.GetRequiredService<IPublisher>().Publish(S.Shipments.Reroute(42));

        Activity publish = spans.Named("publish Rerouted<Int32>");
        Activity[] handlers = [.. spans.Recorded.Where(span => span.OperationName == "handle Rerouted<Int32>")];
        handlers.Select(span => span.GetTagItem("relevox.handler.type")).ShouldBe(
            ["Scenarios.ShipmentTracker<Scenarios.Rerouted<System.Int32>>", "Scenarios.ShipmentAudit<Scenarios.Rerouted<System.Int32>>"],
            ignoreOrder: true);
        foreach (Activity span in handlers)
        {
            span.ParentSpanId.ShouldBe(publish.SpanId);
            span.GetTagItem("relevox.message.type").ShouldBe("Scenarios.Rerouted<System.Int32>");
        }
    }

    [Theory]
    [MemberData(nameof(Publishers))]
    public async Task No_handler_spans_appear_when_the_option_is_off(string publisher)
    {
        using Spans spans = new();
        await using (ServiceProvider provider = Build(publisher, handlerSpans: false))
        {
            IPublisher publish = provider.GetRequiredService<IPublisher>();
            await publish.Publish(new S.Alarm("south"));
            await TelemetryTests.Caught(() => publish.Publish(new S.Brownout("west")));
            await publish.Publish(S.Shipments.Reroute(7));
        }

        spans.Recorded.Select(span => span.OperationName).ShouldBe(
            ["publish Alarm", "publish Brownout", "publish Rerouted<Int32>"],
            ignoreOrder: true);

        // And the option alone, with telemetry off, turns on nothing.
        await using (ServiceProvider off = TelemetryTests.Build(telemetry: false, options => options.Telemetry.NotificationHandlerSpans = true))
        {
            await off.GetRequiredService<IPublisher>().Publish(new S.Alarm("south"));
        }

        spans.Recorded.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Handler_spans_add_no_measurements()
    {
        // The publish is measured once; a measurement per handler would count its time twice.
        using Spans spans = new();
        using Measurements measurements = new();
        await using ServiceProvider provider = Build("ForeachAwait");

        await provider.GetRequiredService<IPublisher>().Publish(new S.Alarm("north"));

        spans.Recorded.ShouldContain(span => span.OperationName == "handle Alarm");
        measurements.Recorded.Select(measurement => measurement.Describe()).ShouldBe(["publish Scenarios.Alarm"]);
    }

    // The trace also records the custom publisher's own call, which is not a handler.
    private static bool Reached(string trace) => !trace.StartsWith("ReversePublisher", StringComparison.Ordinal);

    private static ServiceProvider Build(string publisher, bool handlerSpans = true) =>
        TelemetryTests.Build(configure: options =>
        {
            options.Telemetry.NotificationHandlerSpans = handlerSpans;
            switch (publisher)
            {
                case "TaskWhenAll":
                    options.NotificationPublisher = new TaskWhenAllPublisher();
                    break;
                case "Custom":
                    options.NotificationPublisherType = typeof(S.ReversePublisher);
                    break;
            }
        });
}
