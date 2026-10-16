extern alias oncapar;
extern alias onmediatr;

using System.Reflection;

namespace Capar.DifferentialTests;

/// <summary>
/// Runs every shared scenario on MediatR 12.5.0 and on Capar and requires the same observable outcome.
/// </summary>
/// <remarks>
/// MediatR is the oracle here. A scenario passing means a caller cannot tell the two libraries apart on it:
/// same result, same handlers in the same order, same exception type thrown at the same moment.
/// </remarks>
public sealed class DifferentialTests
{
    private static readonly Type OnMediatR = typeof(onmediatr::Scenarios.Scenarios);
    private static readonly Type OnCapar = typeof(oncapar::Scenarios.Scenarios);

    public static TheoryData<string> ScenarioNames()
    {
        TheoryData<string> names = [];
        foreach (MethodInfo method in OnMediatR.GetMethods(BindingFlags.Public | BindingFlags.Static).OrderBy(method => method.Name, StringComparer.Ordinal))
        {
            names.Add(method.Name);
        }

        return names;
    }

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public async Task Capar_behaves_like_MediatR(string scenario)
    {
        string mediatr = await Invoke(OnMediatR, scenario);
        string capar = await Invoke(OnCapar, scenario);

        capar.ShouldBe(mediatr, $"MediatR: {mediatr}{Environment.NewLine}Capar:   {capar}");
    }

    [Fact]
    public void Both_assemblies_run_the_same_scenarios()
    {
        static string[] Names(Type type) =>
            [.. type.GetMethods(BindingFlags.Public | BindingFlags.Static).Select(method => method.Name).Order(StringComparer.Ordinal)];

        Names(OnCapar).ShouldBe(Names(OnMediatR));
        Names(OnMediatR).Length.ShouldBeGreaterThan(20);
    }

    /// <summary>
    /// Pins down the notification rules the differential test found, so agreement cannot mean "both empty".
    /// </summary>
    /// <remarks>
    /// A handler for a base type or for INotification receives a notification that some handler names
    /// exactly, and receives nothing for a notification no handler names. That second half is MediatR's
    /// registration doing it, not a design anyone would pick, and Capar matches it on purpose.
    /// </remarks>
    [Fact]
    public async Task Base_type_handlers_see_only_directly_handled_notifications()
    {
        string alarm = await Invoke(OnCapar, "PublishToSeveralHandlers");
        alarm.ShouldContain("AlarmLogger(north)");
        alarm.ShouldContain("SignalAuditor(Alarm)");
        alarm.ShouldContain("EverythingWatcher(Alarm)");

        string quiet = await Invoke(OnCapar, "PublishWithOnlyCatchAllHandler");
        quiet.ShouldContain("trace=[]");
    }

    /// <summary>Pins down the pipeline order, so agreement cannot mean "no behaviour ran in either".</summary>
    /// <remarks>
    /// Pre-processors run ahead of every behaviour and post-processors after all of them, because MediatR
    /// registers the processor behaviours before any behaviour from the configuration.
    /// </remarks>
    [Fact]
    public async Task Behaviours_run_outermost_first_in_registration_order()
    {
        string outcome = await Invoke(OnCapar, "PipelineRunsInRegistrationOrder");

        outcome.ShouldBe(
            "result=\"p pong!\" | trace=[PingPre(p), StampPre(Ping), Outer>Ping/String, Inner>, PingAudit>, Late>, PingHandler(p), Late<, Inner<, Outer<, PingPost(p pong!)] | error=none");
    }

    /// <summary>
    /// Pins down MediatR's processor rule: processors found by scanning do not run unless some processor was
    /// added through the configuration, which makes the processor behaviour exist.
    /// </summary>
    [Fact]
    public async Task Scanned_processors_alone_do_not_run()
    {
        string plain = await Invoke(OnCapar, "SendThroughSender");

        plain.ShouldNotContain("PingPre");
        plain.ShouldNotContain("PingPost");
    }

    /// <summary>
    /// Pins down which requests a processor written for an interface reaches: the ones some other processor
    /// names exactly. Cached has CachedPre, so AuditedPre reaches it.
    /// </summary>
    [Fact]
    public async Task An_interface_processor_reaches_requests_that_have_a_processor_of_their_own()
    {
        string cached = await Invoke(OnCapar, "PipelineCanShortCircuit");

        cached.ShouldContain("AuditedPre(Cached), CachedPre");
    }

    /// <summary>
    /// A configured publisher is really used: the reversing one runs the same handlers backwards, and it is
    /// called even for a notification nothing handles.
    /// </summary>
    [Fact]
    public async Task A_configured_publisher_gets_the_handlers()
    {
        static string[] Handlers(string outcome) =>
            [.. outcome[(outcome.IndexOf('[', StringComparison.Ordinal) + 1)..outcome.IndexOf(']', StringComparison.Ordinal)]
                .Split(", ")
                .Select(entry => entry.Contains('(', StringComparison.Ordinal) ? entry[..entry.IndexOf('(', StringComparison.Ordinal)] : entry)
                .Where(name => name.EndsWith("Logger", StringComparison.Ordinal)
                    || name.EndsWith("Pager", StringComparison.Ordinal)
                    || name.EndsWith("Auditor", StringComparison.Ordinal)
                    || name.EndsWith("Watcher", StringComparison.Ordinal))];

        string[] normal = Handlers(await Invoke(OnCapar, "PublishToSeveralHandlers"));
        string[] reversed = Handlers(await Invoke(OnCapar, "PublishWithACustomPublisher"));

        normal.Length.ShouldBeGreaterThan(1);
        reversed.ShouldBe([.. normal.Reverse()]);
        (await Invoke(OnCapar, "PublishWithACustomPublisherAndNoHandlers")).ShouldContain("ReversePublisher(Quiet, 0)");
    }

    private static Task<string> Invoke(Type scenarios, string name) =>
        (Task<string>)scenarios.GetMethod(name, BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!;
}
