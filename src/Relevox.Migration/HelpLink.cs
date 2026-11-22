namespace Relevox.Migration;

/// <summary>
/// Where each rule's help link points: its page on the documentation site, built from the rule's section of docs/diagnostics.md.
/// </summary>
internal static class HelpLink
{
    public static string To(string id) => "https://tunahanaliozturk.github.io/relevox/rules/" + id.ToLowerInvariant() + ".html";
}
