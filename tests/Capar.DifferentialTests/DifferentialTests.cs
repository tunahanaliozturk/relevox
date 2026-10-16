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

    private static Task<string> Invoke(Type scenarios, string name) =>
        (Task<string>)scenarios.GetMethod(name, BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!;
}
