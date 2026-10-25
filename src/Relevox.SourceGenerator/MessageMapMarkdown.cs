using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Relevox.SourceGenerator;

/// <summary>
/// The message map as Markdown, for reading in a pull request (#45): a table per request and stream, with its
/// pipeline in the order it runs, and a Mermaid diagram per notification. It is written from the same values as
/// the JSON, in the same order, so it changes exactly when the JSON does.
/// </summary>
internal static class MessageMapMarkdown
{
    public static string Write(List<(string Name, object? Value)> map)
    {
        StringBuilder markdown = new();
        markdown.Append("# Message map of ").Append(Get(map, "project")).Append("\n\n")
            .Append("Written by the build from what Relevox's generator sees; the same map as JSON is in the file next to this one. ")
            .Append("Each table lists a message's pipeline from top to bottom in the order it runs. Behaviours wrap the handler, outermost first; ")
            .Append("exception handlers and actions wrap the whole pipeline and run only when it throws. They are listed as they run ")
            .Append("with the default RequestExceptionActionProcessorStrategy, handlers first; with ApplyForAllExceptions the actions run first.");
        if (Items(map, "requests").Any(static request => Behaviours(request, outside: true).Any()))
        {
            markdown.Append(" A behaviour ordered below zero runs outside all of them, the processors included.");
        }

        markdown.Append('\n');

        Section(markdown, "Requests", Items(map, "requests"), static (markdown, request) =>
        {
            Intro(markdown, request, Get(request, "response") is string response ? $"Returns {Code(response)}." : "Returns nothing.");
            From(markdown, request, "Sent from");
            // A behaviour ordered below zero runs outside the built-in processor and exception behaviours.
            Table(markdown, [
                .. Behaviours(request, outside: true),
                .. Rows(request, "preProcessors", "Pre-processor"),
                .. Behaviours(request, outside: false),
                Row("Handler", (List<(string, object?)>)Get(request, "handler")!),
                .. Rows(request, "postProcessors", "Post-processor"),
                .. Items(request, "exceptionHandlers").Select(static handler => Row($"Exception handler, {Code((string)Get(handler, "exception")!)}", handler)),
                .. Items(request, "exceptionActions").Select(static action => Row($"Exception action, {Code((string)Get(action, "exception")!)}", action)),
            ]);
        });

        Section(markdown, "Streams", Items(map, "streams"), static (markdown, stream) =>
        {
            Intro(markdown, stream, $"Yields {Code((string)Get(stream, "response")!)}.");
            From(markdown, stream, "Created from");
            Table(markdown, [
                .. Behaviours(stream, outside: null),
                Row("Handler", (List<(string, object?)>)Get(stream, "handler")!),
            ]);
        });

        Section(markdown, "Notifications", Items(map, "notifications"), static (markdown, notification) =>
        {
            Intro(markdown, notification, null);
            From(markdown, notification, "Published from");
            List<List<(string Name, object? Value)>> handlers = Items(notification, "handlers");
            if (handlers.Count == 0)
            {
                markdown.Append("No handlers: publishing it does nothing.\n");
                return;
            }

            // Handlers are numbered in the order they run with the default publisher, one after another.
            markdown.Append("```mermaid\nflowchart LR\n  notification[\"").Append(Label((string)Get(notification, "type")!)).Append("\"]\n");
            for (int index = 0; index < handlers.Count; index++)
            {
                markdown.Append("  notification --> handler").Append(index + 1).Append("[\"").Append(index + 1).Append(". ")
                    .Append(Label((string)Get(handlers[index], "type")!)).Append("\"]\n");
            }

            markdown.Append("```\n");
        });

        List<object?> unread = (List<object?>)Get(map, "unread")!;
        if (unread.Count > 0)
        {
            markdown.Append("\n## Not read\n\nThe build could not read these registrations, so what they add is missing above:\n\n");
            foreach (object? registration in unread)
            {
                markdown.Append("- ").Append(Code((string)registration!)).Append('\n');
            }
        }

        return markdown.ToString();
    }

    private static void Section(StringBuilder markdown, string title, List<List<(string Name, object? Value)>> entries, System.Action<StringBuilder, List<(string Name, object? Value)>> write)
    {
        if (entries.Count == 0)
        {
            return;
        }

        markdown.Append("\n## ").Append(title).Append('\n');
        foreach (List<(string Name, object? Value)> entry in entries)
        {
            markdown.Append("\n### ").Append(Code((string)Get(entry, "type")!)).Append("\n\n");
            write(markdown, entry);
        }
    }

    private static void Intro(StringBuilder markdown, List<(string Name, object? Value)> entry, string? first)
    {
        string line = string.Join(" ", new[] { first, Get(entry, "project") is string project ? $"Declared in {project}." : null }.Where(static part => part is not null));
        if (line.Length > 0)
        {
            markdown.Append(line).Append("\n\n");
        }
    }

    private static void From(StringBuilder markdown, List<(string Name, object? Value)> entry, string verb)
    {
        List<List<(string Name, object? Value)>> sites = Items(entry, "callSites");
        if (sites.Count > 0)
        {
            markdown.Append(verb).Append(' ')
                .Append(string.Join(", ", sites.Select(static site => Code($"{Get(site, "file")}:{((int)Get(site, "line")!).ToString(System.Globalization.CultureInfo.InvariantCulture)}"))))
                .Append(".\n\n");
        }
    }

    private static void Table(StringBuilder markdown, List<string> rows)
    {
        markdown.Append("| Step | Type | Project |\n|---|---|---|\n");
        foreach (string row in rows)
        {
            markdown.Append(row).Append('\n');
        }
    }

    private static IEnumerable<string> Rows(List<(string Name, object? Value)> entry, string field, string step) =>
        Items(entry, field).Select(component => Row(step, component));

    private static IEnumerable<string> Behaviours(List<(string Name, object? Value)> entry, bool? outside) =>
        Items(entry, "behaviors")
            .Select(static behavior => (Behavior: behavior, Order: behavior.Any(static field => field.Name == "order") ? (int?)Get(behavior, "order") : null))
            .Where(behavior => outside is not { } wanted || (behavior.Order < 0) == wanted)
            .Select(static behavior => Row(behavior.Order is { } order ? $"Behaviour, order {order.ToString(System.Globalization.CultureInfo.InvariantCulture)}" : "Behaviour", behavior.Behavior));

    private static string Row(string step, List<(string Name, object? Value)> component) =>
        $"| {step} | {Code((string)Get(component, "type")!)} | {Get(component, "project")} |";

    private static object? Get(List<(string Name, object? Value)> entry, string name) => entry.First(field => field.Name == name).Value;

    private static List<List<(string Name, object? Value)>> Items(List<(string Name, object? Value)> entry, string name) =>
        [.. ((List<object?>)Get(entry, name)!).Cast<List<(string Name, object? Value)>>()];

    // Inline code keeps a generic's angle brackets from being read as HTML. Unread registrations are source text
    // and may hold backticks, which a fence longer than their longest run, and spaces, keep literal.
    private static string Code(string text)
    {
        int longest = 0;
        for (int index = 0, run = 0; index < text.Length; index++)
        {
            run = text[index] == '`' ? run + 1 : 0;
            longest = System.Math.Max(longest, run);
        }

        string fence = new('`', longest + 1);
        return longest == 0 ? $"`{text}`" : $"{fence} {text} {fence}";
    }

    // Mermaid reads quotes and angle brackets in a label as markup; its entity codes keep them literal.
    private static string Label(string type) => type.Replace("\"", "#quot;").Replace("<", "#lt;").Replace(">", "#gt;");
}
