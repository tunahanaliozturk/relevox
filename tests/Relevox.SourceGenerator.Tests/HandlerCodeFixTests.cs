using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Relevox.CodeFixes;

namespace Relevox.SourceGenerator.Tests;

/// <summary>
/// The fix for RVX0001 and the refactorings that write a request's handler, or a handler's request, in a new file.
/// </summary>
/// <remarks>
/// Each test runs the action the way the IDE does, through its operations, and checks the whole new file. Where the
/// result is meant to be finished code, the project is then built with the generator and must have no errors and
/// no Relevox warnings left, so a handler with the wrong signature cannot pass.
/// </remarks>
public sealed class HandlerCodeFixTests
{
    // What ImplicitUsings gives an SDK project, so the expected files show which usings are not repeated.
    private const string ImplicitUsings = """
        global using System;
        global using System.Collections.Generic;
        global using System.Threading;
        global using System.Threading.Tasks;

        """;

    private static readonly AdhocWorkspace Host = new();

    // Stands for RVX0001 where a test reports it by hand, to ask the fix about a declaration the analyzer skips.
    private static readonly DiagnosticDescriptor Rvx0001 = new RelevoxAnalyzer().SupportedDiagnostics.Single(static rule => rule.Id == "RVX0001");

    public static TheoryData<string, string, string> Shapes => new()
    {
        {
            "public sealed record Ping(int Value) : IRequest<int>;",
            "IRequestHandler<Ping, int>",
            "public Task<int> Handle(Ping request, CancellationToken cancellationToken)"
        },
        {
            "public sealed record Ring : IRequest;",
            "IRequestHandler<Ring>",
            "public Task Handle(Ring request, CancellationToken cancellationToken)"
        },
        {
            "public sealed record Numbers(int Count) : IStreamRequest<int>;",
            "IStreamRequestHandler<Numbers, int>",
            "public IAsyncEnumerable<int> Handle(Numbers request, CancellationToken cancellationToken)"
        },
        {
            "public sealed record Unitless : IRequest<Unit>;",
            "IRequestHandler<Unitless, Unit>",
            "public Task<Unit> Handle(Unitless request, CancellationToken cancellationToken)"
        },
        {
            "public sealed class Lookup : IRequest<string?>;",
            "IRequestHandler<Lookup, string?>",
            "public Task<string?> Handle(Lookup request, CancellationToken cancellationToken)"
        },
        {
            "public readonly record struct Tick : IRequest<long>;",
            "IRequestHandler<Tick, long>",
            "public Task<long> Handle(Tick request, CancellationToken cancellationToken)"
        },
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task The_fix_writes_a_handler_for_each_shape_of_request(string request, string handler, string signature)
    {
        Project project = WithImplicitUsings(("Requests/Message.cs", "using Relevox;\n\nnamespace Shop;\n\n" + request + "\n"));
        string name = request.Split(' ').First(static word => char.IsUpper(word[0])).Split('(', ';')[0];

        CodeAction action = (await Fixes(project, "Requests/Message.cs")).ShouldHaveSingleItem();
        action.Title.ShouldBe($"Add handler '{name}Handler'");
        Project fixedProject = await Apply(action, project);

        (await Text(fixedProject, $"Requests/{name}Handler.cs")).ShouldBe($$"""
            using Relevox;

            namespace Shop;

            public sealed class {{name}}Handler : {{handler}}
            {
                {{signature}}
                {
                    throw new NotImplementedException();
                }
            }

            """);
        Document written = Doc(fixedProject, $"Requests/{name}Handler.cs");
        Path.GetFullPath(written.FilePath!).ShouldBe(Path.GetFullPath($"/repo/Shop/Requests/{name}Handler.cs"));
        await ShouldBuildClean(fixedProject);
    }

    [Fact]
    public async Task Usings_the_project_does_not_already_have_are_added_sorted()
    {
        Project project = Shop(
            ("Billing/Receipt.cs", "namespace Shop.Billing;\n\npublic sealed record Receipt(decimal Total);\n"),
            ("Orders/Place.cs", "using Relevox;\nusing Shop.Billing;\n\nnamespace Shop.Orders;\n\npublic sealed record Place(int Id) : IRequest<Receipt>;\n"));

        Project fixedProject = await Apply((await Fixes(project, "Orders/Place.cs")).ShouldHaveSingleItem(), project);

        (await Text(fixedProject, "Orders/PlaceHandler.cs")).ShouldBe("""
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Relevox;
            using Shop.Billing;

            namespace Shop.Orders;

            public sealed class PlaceHandler : IRequestHandler<Place, Receipt>
            {
                public Task<Receipt> Handle(Place request, CancellationToken cancellationToken)
                {
                    throw new NotImplementedException();
                }
            }

            """);
        await ShouldBuildClean(fixedProject);
    }

    [Fact]
    public async Task A_block_namespace_is_matched()
    {
        Project project = WithImplicitUsings(("Ping.cs", "using Relevox;\n\nnamespace Shop\n{\n    public sealed record Ping : IRequest<int>;\n}\n"));

        Project fixedProject = await Apply((await Fixes(project, "Ping.cs")).ShouldHaveSingleItem(), project);

        (await Text(fixedProject, "PingHandler.cs")).ShouldBe("""
            using Relevox;

            namespace Shop
            {
                public sealed class PingHandler : IRequestHandler<Ping, int>
                {
                    public Task<int> Handle(Ping request, CancellationToken cancellationToken)
                    {
                        throw new NotImplementedException();
                    }
                }
            }

            """);
        await ShouldBuildClean(fixedProject);
    }

    [Fact]
    public async Task A_request_in_the_global_namespace_gets_a_handler_there()
    {
        Project project = WithImplicitUsings(("Ping.cs", "using Relevox;\n\npublic sealed record Ping : IRequest<int>;\n"));

        Project fixedProject = await Apply((await Fixes(project, "Ping.cs")).ShouldHaveSingleItem(), project);

        (await Text(fixedProject, "PingHandler.cs")).ShouldStartWith("using Relevox;\n\npublic sealed class PingHandler : IRequestHandler<Ping, int>\n");
        await ShouldBuildClean(fixedProject);
    }

    [Fact]
    public async Task An_internal_request_gets_an_internal_handler()
    {
        Project project = WithImplicitUsings(("Ping.cs", "using Relevox;\n\nnamespace Shop;\n\ninternal sealed record Ping : IRequest<int>;\n"));

        Project fixedProject = await Apply((await Fixes(project, "Ping.cs")).ShouldHaveSingleItem(), project);

        (await Text(fixedProject, "PingHandler.cs")).ShouldContain("internal sealed class PingHandler : IRequestHandler<Ping, int>");
        await ShouldBuildClean(fixedProject);
    }

    [Fact]
    public async Task A_nested_request_gets_a_top_level_handler_that_names_it_through_its_container()
    {
        Project project = WithImplicitUsings(("Orders.cs", "using Relevox;\n\nnamespace Shop;\n\npublic static class Orders\n{\n    public sealed record Place(int Id) : IRequest<int>;\n}\n"));

        Project fixedProject = await Apply((await Fixes(project, "Orders.cs")).ShouldHaveSingleItem(), project);

        (await Text(fixedProject, "PlaceHandler.cs")).ShouldContain("public sealed class PlaceHandler : IRequestHandler<Orders.Place, int>");
        (await Text(fixedProject, "PlaceHandler.cs")).ShouldContain("public Task<int> Handle(Orders.Place request, CancellationToken cancellationToken)");
        await ShouldBuildClean(fixedProject);
    }

    [Fact]
    public async Task A_name_taken_by_a_file_in_the_folder_or_a_type_in_the_namespace_gets_a_number()
    {
        Project project = WithImplicitUsings(
            ("Requests/Ping.cs", "using Relevox;\n\nnamespace Shop;\n\npublic sealed record Ping : IRequest<int>;\n"),
            ("Requests/PingHandler.cs", "// Kept for the history.\n"),
            ("Other/Taken.cs", "namespace Shop;\n\npublic sealed class PingHandler2;\n"));

        CodeAction action = (await Fixes(project, "Requests/Ping.cs")).ShouldHaveSingleItem();
        action.Title.ShouldBe("Add handler 'PingHandler3'");
        Project fixedProject = await Apply(action, project);

        (await Text(fixedProject, "Requests/PingHandler.cs")).ShouldBe("// Kept for the history.\n");
        (await Text(fixedProject, "Requests/PingHandler3.cs")).ShouldContain("public sealed class PingHandler3 : IRequestHandler<Ping, int>");
        await ShouldBuildClean(fixedProject);
    }

    [Fact]
    public async Task The_new_file_ends_its_lines_like_the_request_file()
    {
        Project project = WithImplicitUsings(("Ping.cs", "using Relevox;\r\n\r\nnamespace Shop;\r\n\r\npublic sealed record Ping : IRequest<int>;\r\n"));

        Project fixedProject = await Apply((await Fixes(project, "Ping.cs")).ShouldHaveSingleItem(), project);

        string text = await Text(fixedProject, "PingHandler.cs");
        text.ShouldContain("\r\n");
        text.Replace("\r\n", string.Empty, StringComparison.Ordinal).ShouldNotContain("\n");
    }

    [Fact]
    public void The_fix_has_no_fix_all_so_it_cannot_silence_every_RVX0001_with_handlers_that_throw() =>
        new WriteMissingHandler().GetFixAllProvider().ShouldBeNull();

    [Fact]
    public async Task A_generic_request_is_not_reported_and_no_handler_is_offered_for_it()
    {
        // Reached through its closed forms (ADR 0008); a generic handler for it is a choice a template should not make.
        Project project = WithImplicitUsings(("Page.cs", "using Relevox;\n\nnamespace Shop;\n\npublic sealed record Page<T>(int Size) : IRequest<List<T>>;\n"));

        (await ReportedIds(project)).ShouldNotContain("RVX0001");
        (await Refactorings(project, "Page.cs", "Page<T>")).ShouldBeEmpty();
        (await Refactorings(project, "Page.cs", "Page<T>", asFix: true)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("public static class Orders\n{\n    private sealed record Place : IRequest<int>;\n}")]
    [InlineData("public class Orders\n{\n    protected sealed record Place : IRequest<int>;\n}")]
    [InlineData("public static class Orders\n{\n    private static class Inner\n    {\n        public sealed record Place : IRequest<int>;\n    }\n}")]
    [InlineData("public static class Orders<T>\n{\n    public sealed record Place : IRequest<int>;\n}")]
    [InlineData("file sealed record Place : IRequest<int>;")]
    [InlineData("public static class Orders\n{\n    public ref struct Place : IRequest<int>;\n}")]
    public async Task A_request_a_handler_in_another_file_could_not_name_or_take_is_not_offered_one(string declaration)
    {
        // Private, protected or file-local: the new file cannot name it. Nested in a generic type: reached through
        // closed forms, as a generic request is. A ref struct cannot be a type argument of the handler interface.
        Project project = WithImplicitUsings(("Orders.cs", "using Relevox;\n\nnamespace Shop;\n\n" + declaration + "\n"));

        (await Refactorings(project, "Orders.cs", "Place :")).ShouldBeEmpty();
        (await Refactorings(project, "Orders.cs", "Place :", asFix: true)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_file_a_tool_writes_is_not_offered_anything()
    {
        Project project = WithImplicitUsings(
            ("Ping.g.cs", "using Relevox;\n\nnamespace Shop;\n\npublic sealed record Ping : IRequest<int>;\n\npublic sealed record Pong;\n"),
            ("PlaceHandler.g.cs", "using Relevox;\n\nnamespace Shop;\n\npublic sealed class PlaceHandler : IRequestHandler<Place>\n{\n    public Task Handle(Place request, CancellationToken cancellationToken) => Task.CompletedTask;\n}\n"));

        (await Refactorings(project, "Ping.g.cs", "Ping :")).ShouldBeEmpty();
        (await Refactorings(project, "Ping.g.cs", "Ping :", asFix: true)).ShouldBeEmpty();
        (await Refactorings(project, "Ping.g.cs", "Pong;")).ShouldBeEmpty();
        (await Refactorings(project, "PlaceHandler.g.cs", "PlaceHandler :")).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_diagnostic_from_another_file_gets_no_fix()
    {
        Project project = WithImplicitUsings(
            ("Ping.cs", "using Relevox;\n\nnamespace Shop;\n\npublic sealed record Ping : IRequest<int>;\n"),
            ("Other.cs", "using Relevox;\n\nnamespace Shop;\n\npublic sealed record Pong : IRequest<int>;\n\npublic sealed record Long : IRequest<int>;\n"));
        Diagnostic fromOther = (await Diagnostics(project)).Single(diagnostic => diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture).Contains("Long", StringComparison.Ordinal));

        (await FixesFor(Doc(project, "Ping.cs"), fromOther)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("public sealed record ( : IRequest<int>;", "record", 0)]
    [InlineData("public sealed class PlaceHandler : IRequestHandler<Place", "PlaceHandler", 1)]
    [InlineData("public sealed class PlaceHandler : IRequestHandler<, int>\n{\n}", "PlaceHandler", 0)]
    [InlineData("public sealed class PlaceHandler : IRequestHandler<Place, int, int>\n{\n}", "PlaceHandler", 0)]
    [InlineData("public sealed record Ping : IRequest<int>, IRequest<long>;", "Ping", 0)]
    public async Task Odd_or_malformed_syntax_does_not_throw_and_offers_only_what_makes_sense(string source, string at, int offers)
    {
        const string path = "Ping.cs";
        Project project = WithImplicitUsings((path, "using Relevox;\n\nnamespace Shop;\n\n" + source + "\n"));
        Document document = Doc(project, path);
        string text = (await document.GetTextAsync()).ToString();

        CodeAction[] offered = await Refactorings(project, path, at);
        foreach (CodeAction action in offered)
        {
            await action.GetOperationsAsync(CancellationToken.None);
        }

        offered.Length.ShouldBe(offers);
        List<CodeAction> outside = [];
        await new RequestRefactorings().ComputeRefactoringsAsync(new CodeRefactoringContext(document, new TextSpan(text.Length + 10, 0), outside.Add, CancellationToken.None));
        outside.ShouldBeEmpty();

        SyntaxTree tree = (await document.GetSyntaxTreeAsync())!;
        Diagnostic whole = Diagnostic.Create(Rvx0001, Location.Create(tree, new TextSpan(0, text.Length)));
        (await FixesFor(document, whole)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_record_becomes_a_request()
    {
        Project project = WithImplicitUsings(("Ping.cs", "namespace Shop;\n\npublic sealed record Ping(int Value);\n"));

        CodeAction[] actions = await Refactorings(project, "Ping.cs", "Ping(");
        actions.Select(static action => action.Title).ShouldBe(["Make 'Ping' a request", "Make 'Ping' a request with handler 'PingHandler'"]);
        Project changed = await Apply(actions[0], project);

        (await Text(changed, "Ping.cs")).ShouldBe("using Relevox;\n\nnamespace Shop;\n\npublic sealed record Ping(int Value) : IRequest;\n");
        changed.Documents.Count().ShouldBe(2);
    }

    [Fact]
    public async Task A_class_with_a_base_list_and_a_body_becomes_a_request_with_its_handler()
    {
        Project project = WithImplicitUsings(("Requests/Ping.cs", """
            namespace Shop;

            public abstract class Message;

            public sealed class Ping : Message
            {
                public int Value { get; init; }
            }

            """));

        CodeAction action = (await Refactorings(project, "Requests/Ping.cs", "Ping :"))[1];
        Project changed = await Apply(action, project);

        (await Text(changed, "Requests/Ping.cs")).ShouldBe("""
            using Relevox;

            namespace Shop;

            public abstract class Message;

            public sealed class Ping : Message, IRequest
            {
                public int Value { get; init; }
            }

            """);
        (await Text(changed, "Requests/PingHandler.cs")).ShouldContain("public sealed class PingHandler : IRequestHandler<Ping>");
        await ShouldBuildClean(changed);
    }

    [Fact]
    public async Task A_request_with_no_handler_is_offered_one_without_waiting_for_RVX0001()
    {
        Project project = WithImplicitUsings(("Ping.cs", "using Relevox;\n\nnamespace Shop;\n\npublic sealed record Ping : IRequest<int>;\n"));

        CodeAction action = (await Refactorings(project, "Ping.cs", "Ping :")).ShouldHaveSingleItem();
        action.Title.ShouldBe("Add handler 'PingHandler'");
        await ShouldBuildClean(await Apply(action, project));
    }

    [Fact]
    public async Task Nothing_is_offered_on_a_request_that_has_a_handler_on_a_notification_or_inside_a_body()
    {
        Project project = WithImplicitUsings(("Ping.cs", """
            using Relevox;

            namespace Shop;

            public sealed record Ping : IRequest<int>;

            public sealed record Placed : INotification;

            public sealed class PingHandler : IRequestHandler<Ping, int>
            {
                public Task<int> Handle(Ping request, CancellationToken cancellationToken) => Task.FromResult(1);
            }

            public sealed class Plain
            {
                public int Value { get; init; }
            }

            """));

        (await Refactorings(project, "Ping.cs", "Ping :")).ShouldBeEmpty();
        (await Refactorings(project, "Ping.cs", "Placed :")).ShouldBeEmpty();
        (await Refactorings(project, "Ping.cs", "PingHandler :")).ShouldBeEmpty();
        (await Refactorings(project, "Ping.cs", "Value")).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("public abstract record Command;")]
    [InlineData("public static class Commands;")]
    [InlineData("public sealed record Wrapper<T>(T Value);")]
    [InlineData("public interface ICommand;")]
    public async Task Types_a_handler_cannot_be_written_for_are_not_made_requests(string declaration)
    {
        Project project = WithImplicitUsings(("Type.cs", "namespace Shop;\n\n" + declaration + "\n"));

        (await Refactorings(project, "Type.cs", "public")).ShouldBeEmpty();
    }

    public static TheoryData<string, string, string> Handlers => new()
    {
        {
            "public sealed class PlaceHandler : IRequestHandler<Place, Receipt>\n{\n    public Task<Receipt> Handle(Place request, CancellationToken cancellationToken) => Task.FromResult(new Receipt(1));\n}",
            "Add request 'Place'",
            "using Relevox;\nusing Shop.Billing;\n\nnamespace Shop.Orders;\n\npublic sealed record Place : IRequest<Receipt>;\n"
        },
        {
            "public sealed class PlaceHandler : IRequestHandler<Place>\n{\n    public Task Handle(Place request, CancellationToken cancellationToken) => Task.CompletedTask;\n}",
            "Add request 'Place'",
            "using Relevox;\n\nnamespace Shop.Orders;\n\npublic sealed record Place : IRequest;\n"
        },
        {
            "public sealed class PlaceHandler : IStreamRequestHandler<Place, int>\n{\n    public async IAsyncEnumerable<int> Handle(Place request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)\n    {\n        await Task.Yield();\n        yield return 1;\n    }\n}",
            "Add stream request 'Place'",
            "using Relevox;\n\nnamespace Shop.Orders;\n\npublic sealed record Place : IStreamRequest<int>;\n"
        },
        {
            "public sealed class PlaceHandler : INotificationHandler<Place>\n{\n    public Task Handle(Place notification, CancellationToken cancellationToken) => Task.CompletedTask;\n}",
            "Add notification 'Place'",
            "using Relevox;\n\nnamespace Shop.Orders;\n\npublic sealed record Place : INotification;\n"
        },
        {
            "internal sealed class PlaceHandler : IRequestHandler<Place, string?>\n{\n    public Task<string?> Handle(Place request, CancellationToken cancellationToken) => Task.FromResult<string?>(null);\n}",
            "Add request 'Place'",
            "using Relevox;\n\nnamespace Shop.Orders;\n\ninternal sealed record Place : IRequest<string?>;\n"
        },
    };

    [Theory]
    [MemberData(nameof(Handlers))]
    public async Task A_handler_whose_message_does_not_exist_gets_it_written(string handler, string title, string expected)
    {
        Project project = WithImplicitUsings(
            ("Billing/Receipt.cs", "namespace Shop.Billing;\n\npublic sealed record Receipt(decimal Total);\n"),
            ("Orders/PlaceHandler.cs", "using Relevox;\nusing Shop.Billing;\n\nnamespace Shop.Orders;\n\n" + handler + "\n"));

        CodeAction action = (await Refactorings(project, "Orders/PlaceHandler.cs", "PlaceHandler :")).ShouldHaveSingleItem();
        action.Title.ShouldBe(title);
        Project changed = await Apply(action, project);

        (await Text(changed, "Orders/Place.cs")).ShouldBe(expected);
        await ShouldBuildClean(changed);
    }

    [Fact]
    public async Task The_request_can_be_written_from_the_handler_interface_too()
    {
        Project project = WithImplicitUsings(("PlaceHandler.cs", "using Relevox;\n\nnamespace Shop;\n\npublic sealed class PlaceHandler : IRequestHandler<Place, int>\n{\n    public Task<int> Handle(Place request, CancellationToken cancellationToken) => Task.FromResult(1);\n}\n"));

        CodeAction action = (await Refactorings(project, "PlaceHandler.cs", "Place, int")).ShouldHaveSingleItem();
        action.Title.ShouldBe("Add request 'Place'");
        Project changed = await Apply(action, project);

        (await Text(changed, "Place.cs")).ShouldBe("using Relevox;\n\nnamespace Shop;\n\npublic sealed record Place : IRequest<int>;\n");
        await ShouldBuildClean(changed);
    }

    [Fact]
    public async Task A_request_named_with_a_keyword_keeps_its_escape()
    {
        Project project = WithImplicitUsings(("PlaceHandler.cs", "using Relevox;\n\nnamespace Shop;\n\npublic sealed class PlaceHandler : IRequestHandler<@event, int>\n{\n    public Task<int> Handle(@event request, CancellationToken cancellationToken) => Task.FromResult(1);\n}\n"));

        Project changed = await Apply((await Refactorings(project, "PlaceHandler.cs", "PlaceHandler :")).ShouldHaveSingleItem(), project);

        (await Text(changed, "event.cs")).ShouldBe("using Relevox;\n\nnamespace Shop;\n\npublic sealed record @event : IRequest<int>;\n");
        await ShouldBuildClean(changed);
    }

    [Theory]
    [InlineData("public sealed class PlaceHandler<T> : IRequestHandler<Place, T>")]
    [InlineData("public sealed class PlaceHandler<T> : IRequestHandler<Place, List<T>>")]
    [InlineData("public static class Outer<T>\n{\n    public sealed class PlaceHandler : IStreamRequestHandler<Place, T[]>")]
    public async Task A_missing_request_whose_response_names_a_type_parameter_is_left_to_a_person(string handler)
    {
        Project project = WithImplicitUsings(("PlaceHandler.cs", "using Relevox;\n\nnamespace Shop;\n\n" + handler + "\n{\n}\n" + (handler.Contains("Outer", StringComparison.Ordinal) ? "}\n" : string.Empty)));

        (await Refactorings(project, "PlaceHandler.cs", "PlaceHandler")).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_global_using_for_Relevox_is_not_repeated()
    {
        Project project = Shop(
            ("Usings.cs", ImplicitUsings + "global using Relevox;\n"),
            ("Ping.cs", "namespace Shop;\n\npublic sealed record Ping : IRequest<int>;\n\npublic sealed record Pong(int Value);\n"));

        Project fixedProject = await Apply((await Fixes(project, "Ping.cs")).ShouldHaveSingleItem(), project);
        (await Text(fixedProject, "PingHandler.cs")).ShouldStartWith("namespace Shop;\n\npublic sealed class PingHandler : IRequestHandler<Ping, int>\n");

        Project changed = await Apply((await Refactorings(project, "Ping.cs", "Pong("))[0], project);
        (await Text(changed, "Ping.cs")).ShouldBe("namespace Shop;\n\npublic sealed record Ping : IRequest<int>;\n\npublic sealed record Pong(int Value) : IRequest;\n");
    }

    [Theory]
    [InlineData("public interface IPingHandler : IRequestHandler<Ping, int>;")]
    [InlineData("public abstract class PingHandlerBase : IRequestHandler<Ping, int>\n{\n    public abstract Task<int> Handle(Ping request, CancellationToken cancellationToken);\n}")]
    [InlineData("public static class Outer\n{\n    private sealed class Hidden : IRequestHandler<Ping, int>\n    {\n        public Task<int> Handle(Ping request, CancellationToken cancellationToken) => Task.FromResult(1);\n    }\n}")]
    public async Task A_handler_the_generator_would_not_dispatch_to_does_not_hide_Add_handler(string notAHandler)
    {
        Project project = WithImplicitUsings(("Ping.cs", "using Relevox;\n\nnamespace Shop;\n\npublic sealed record Ping : IRequest<int>;\n\n" + notAHandler + "\n"));

        (await ReportedIds(project)).ShouldContain("RVX0001");
        (await Refactorings(project, "Ping.cs", "Ping :")).ShouldHaveSingleItem().Title.ShouldBe("Add handler 'PingHandler'");
    }

    [Fact]
    public async Task A_file_already_named_like_the_request_is_left_alone()
    {
        Project project = WithImplicitUsings(
            ("PlaceHandler.cs", "using Relevox;\n\nnamespace Shop;\n\npublic sealed class PlaceHandler : IRequestHandler<Place>\n{\n    public Task Handle(Place request, CancellationToken cancellationToken) => Task.CompletedTask;\n}\n"),
            ("Place.cs", "// Excluded from the build on purpose.\n"));

        Project changed = await Apply((await Refactorings(project, "PlaceHandler.cs", "PlaceHandler :")).ShouldHaveSingleItem(), project);

        (await Text(changed, "Place.cs")).ShouldBe("// Excluded from the build on purpose.\n");
        (await Text(changed, "Place2.cs")).ShouldContain("public sealed record Place : IRequest;");
        await ShouldBuildClean(changed);
    }

    [Theory]
    [InlineData("IRequestHandler<Page<int>, int>")]
    [InlineData("IRequestHandler<Orders.Place, int>")]
    [InlineData("IRequestHandler<global::Place, int>")]
    public async Task A_missing_request_named_other_than_plainly_is_left_to_a_person(string contract)
    {
        Project project = WithImplicitUsings(("PlaceHandler.cs", $$"""
            using Relevox;

            namespace Shop;

            public sealed class PlaceHandler : {{contract}}
            {
            }

            """));

        (await Refactorings(project, "PlaceHandler.cs", "PlaceHandler :")).ShouldBeEmpty();
    }

    private static Project WithImplicitUsings(params (string Path, string Text)[] files) => Shop([("Usings.cs", ImplicitUsings), .. files]);

    private static Project Shop(params (string Path, string Text)[] files)
    {
        Project project = Host.CurrentSolution.AddProject("Shop", "Shop", LanguageNames.CSharp)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable))
            .WithParseOptions(new CSharpParseOptions(LanguageVersion.Preview))
            .WithMetadataReferences(GeneratorHarness.References);
        foreach ((string path, string text) in files)
        {
            string[] parts = path.Split('/');
            project = project.AddDocument(parts[^1], SourceText.From(text), parts[..^1], "/repo/Shop/" + path).Project;
        }

        return project;
    }

    private static async Task<ImmutableArray<Diagnostic>> Diagnostics(Project project)
    {
        Compilation compilation = (await project.GetCompilationAsync())!;
        return await compilation.WithAnalyzers([new RelevoxAnalyzer()]).GetAnalyzerDiagnosticsAsync();
    }

    private static async Task<string[]> ReportedIds(Project project) => [.. (await Diagnostics(project)).Select(static diagnostic => diagnostic.Id)];

    /// <summary>What the IDE offers for each RVX0001 the analyzer reports in the file.</summary>
    private static async Task<CodeAction[]> Fixes(Project project, string path)
    {
        Document document = Doc(project, path);
        SyntaxTree tree = (await document.GetSyntaxTreeAsync())!;

        List<CodeAction> actions = [];
        foreach (Diagnostic diagnostic in (await Diagnostics(project)).Where(diagnostic => diagnostic.Id == "RVX0001" && diagnostic.Location.SourceTree == tree))
        {
            actions.AddRange(await FixesFor(document, diagnostic));
        }

        return [.. actions];
    }

    private static async Task<CodeAction[]> FixesFor(Document document, Diagnostic diagnostic)
    {
        List<CodeAction> actions = [];
        await new WriteMissingHandler().RegisterCodeFixesAsync(new CodeFixContext(document, diagnostic, (action, _) => actions.Add(action), CancellationToken.None));
        return [.. actions];
    }

    /// <summary>
    /// What the IDE offers with the caret where <paramref name="at"/> starts. With <paramref name="asFix"/>, the
    /// RVX0001 fix is asked instead, as if the rule had been reported on the word <paramref name="at"/> starts with.
    /// </summary>
    private static async Task<CodeAction[]> Refactorings(Project project, string path, string at, bool asFix = false)
    {
        Document document = Doc(project, path);
        string text = (await document.GetTextAsync()).ToString();
        TextSpan span = new(text.IndexOf(at, StringComparison.Ordinal), 0);

        if (asFix)
        {
            SyntaxTree tree = (await document.GetSyntaxTreeAsync())!;
            int length = at.IndexOfAny([' ', '<', ';']) is var end and > 0 ? end : at.Length;
            return await FixesFor(document, Diagnostic.Create(Rvx0001, Location.Create(tree, new TextSpan(span.Start, length))));
        }

        List<CodeAction> actions = [];
        await new RequestRefactorings().ComputeRefactoringsAsync(new CodeRefactoringContext(document, span, actions.Add, CancellationToken.None));
        return [.. actions];
    }

    private static async Task<Project> Apply(CodeAction action, Project project)
    {
        ImmutableArray<CodeActionOperation> operations = await action.GetOperationsAsync(CancellationToken.None);
        return operations.OfType<ApplyChangesOperation>().Single().ChangedSolution.GetProject(project.Id)!;
    }

    private static Document Doc(Project project, string path)
    {
        string[] parts = path.Split('/');
        return project.Documents.Single(document => document.Name == parts[^1] && document.Folders.SequenceEqual(parts[..^1]));
    }

    private static async Task<string> Text(Project project, string path) => (await Doc(project, path).GetTextAsync()).ToString();

    /// <summary>Builds the project with the generator: no errors, and no Relevox rule left to report.</summary>
    private static async Task ShouldBuildClean(Project project)
    {
        Compilation compilation = (await project.GetCompilationAsync())!;
        CSharpGeneratorDriver
            .Create([new RelevoxGenerator().AsSourceGenerator()], parseOptions: new CSharpParseOptions(LanguageVersion.Preview))
            .RunGeneratorsAndUpdateCompilation(compilation, out Compilation output, out _);

        output.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Select(static diagnostic => diagnostic.ToString()).ShouldBeEmpty();
        (await output.WithAnalyzers([new RelevoxAnalyzer()]).GetAnalyzerDiagnosticsAsync()).Select(static diagnostic => diagnostic.ToString()).ShouldBeEmpty();
    }
}
