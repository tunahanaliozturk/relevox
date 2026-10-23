using Microsoft.CodeAnalysis;

namespace Relevox.SourceGenerator.Tests;

/// <summary>
/// The message map: its way out of the build (ADR 0014) and what it says about each message (ADR 0015). The
/// expected maps are snapshots; a change to one is a change to the schema's output and belongs in the changelog.
/// </summary>
public sealed class MessageMapTests
{
    private const string Handlers = """
        using Microsoft.Extensions.DependencyInjection; using Relevox; using System.Threading; using System.Threading.Tasks;
        namespace Shop;
        public sealed record Ping(string Text) : IRequest<string>;
        public sealed class PingHandler : IRequestHandler<Ping, string>
        {
            public Task<string> Handle(Ping request, CancellationToken cancellationToken) => Task.FromResult(request.Text);
        }
        public sealed record Touch : IRequest;
        public sealed class TouchHandler : IRequestHandler<Touch>
        {
            public Task Handle(Touch request, CancellationToken cancellationToken) => Task.CompletedTask;
        }
        """;

    // Two open behaviours, one constrained to commands, a closed behaviour, a scanned pre-processor turned on
    // through the options, and an exception handler.
    private const string Pipeline = """
        using System; using System.Threading; using System.Threading.Tasks;
        using Microsoft.Extensions.DependencyInjection; using Relevox; using Relevox.Pipeline;
        namespace Shop;
        public interface ICommand;
        public sealed record Place(int Id) : IRequest<int>, ICommand;
        public sealed class PlaceHandler : IRequestHandler<Place, int>
        {
            public Task<int> Handle(Place request, CancellationToken cancellationToken) => Task.FromResult(request.Id);
        }
        public sealed record Look(int Id) : IRequest<string>;
        public sealed class LookHandler : IRequestHandler<Look, string>
        {
            public Task<string> Handle(Look request, CancellationToken cancellationToken) => Task.FromResult("x");
        }
        public sealed class Logging<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
        {
            public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken) => next(cancellationToken);
        }
        public sealed class Transaction<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : ICommand
        {
            public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken) => next(cancellationToken);
        }
        public sealed class LookCache : IPipelineBehavior<Look, string>
        {
            public Task<string> Handle(Look request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken) => next(cancellationToken);
        }
        public sealed class Audit : IRequestPreProcessor<Place>
        {
            public Task Process(Place request, CancellationToken cancellationToken) => Task.CompletedTask;
        }
        public sealed class Refused : IRequestExceptionHandler<Place, int, InvalidOperationException>
        {
            public Task Handle(Place request, InvalidOperationException exception, RequestExceptionHandlerState<int> state, CancellationToken cancellationToken) => Task.CompletedTask;
        }
        public static class Setup
        {
            public static IServiceCollection Add(IServiceCollection services) => services.AddRelevox(options =>
            {
                options.AutoRegisterRequestProcessors = true;
                options.AddOpenBehavior(typeof(Logging<,>)).AddOpenBehavior(typeof(Transaction<,>));
                options.AddBehavior<LookCache>();
                options.AddRequestPreProcessor<Audit>();
            });
        }
        """;

    [Fact]
    public void A_request_lists_its_handler_and_what_runs_around_it_in_order()
    {
        GeneratorRun run = GeneratorHarness.RunWithMessageMap(Pipeline);

        run.CompilationErrors.ShouldBeEmpty();
        Json(run.Compilation).ShouldBe(Lf("""
            {
              "schemaVersion": 1,
              "project": "Subject",
              "requests": [
                {
                  "type": "Shop.Look",
                  "response": "string",
                  "project": "Subject",
                  "handler": {
                    "type": "Shop.LookHandler",
                    "project": "Subject"
                  },
                  "behaviors": [
                    {
                      "type": "Shop.Logging<Shop.Look, string>",
                      "project": "Subject"
                    },
                    {
                      "type": "Shop.LookCache",
                      "project": "Subject"
                    }
                  ],
                  "preProcessors": [],
                  "postProcessors": [],
                  "exceptionActions": [],
                  "exceptionHandlers": []
                },
                {
                  "type": "Shop.Place",
                  "response": "int",
                  "project": "Subject",
                  "handler": {
                    "type": "Shop.PlaceHandler",
                    "project": "Subject"
                  },
                  "behaviors": [
                    {
                      "type": "Shop.Logging<Shop.Place, int>",
                      "project": "Subject"
                    },
                    {
                      "type": "Shop.Transaction<Shop.Place, int>",
                      "project": "Subject"
                    }
                  ],
                  "preProcessors": [
                    {
                      "type": "Shop.Audit",
                      "project": "Subject"
                    }
                  ],
                  "postProcessors": [],
                  "exceptionActions": [],
                  "exceptionHandlers": [
                    {
                      "type": "Shop.Refused",
                      "project": "Subject",
                      "exception": "System.InvalidOperationException"
                    }
                  ]
                }
              ],
              "streams": [],
              "notifications": [],
              "unread": []
            }

            """));
    }

    [Fact]
    public void Notifications_streams_and_generic_handlers_closed_at_build_time()
    {
        GeneratorRun run = GeneratorHarness.RunWithMessageMap("""
            using System.Collections.Generic; using System.Runtime.CompilerServices; using System.Threading; using System.Threading.Tasks;
            using Microsoft.Extensions.DependencyInjection; using Relevox;
            namespace Shop;
            public interface IEntity;
            public sealed record Order : IEntity;
            public sealed record Load<T>(int Id) : IRequest<T?> where T : IEntity;
            public sealed class LoadHandler<T> : IRequestHandler<Load<T>, T?> where T : IEntity
            {
                public Task<T?> Handle(Load<T> request, CancellationToken cancellationToken) => Task.FromResult(default(T));
            }
            public sealed record Placed(int Id) : INotification;
            public sealed class Email : INotificationHandler<Placed>
            {
                public Task Handle(Placed notification, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            public sealed class Ledger : INotificationHandler<INotification>
            {
                public Task Handle(INotification notification, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            public sealed record Tail(int From) : IStreamRequest<string>;
            public sealed class TailHandler : IStreamRequestHandler<Tail, string>
            {
                public async IAsyncEnumerable<string> Handle(Tail request, [EnumeratorCancellation] CancellationToken cancellationToken)
                {
                    await Task.Yield();
                    yield return "x";
                }
            }
            public sealed class Throttle<TRequest, TResponse> : IStreamPipelineBehavior<TRequest, TResponse> where TRequest : IStreamRequest<TResponse>
            {
                public IAsyncEnumerable<TResponse> Handle(TRequest request, StreamHandlerDelegate<TResponse> next, CancellationToken cancellationToken) => next();
            }
            public static class Setup
            {
                public static IServiceCollection Add(IServiceCollection services) => services.AddRelevox(options => options.AddOpenStreamBehavior(typeof(Throttle<,>)));
                public static Load<Order> Uses() => new(1);
            }
            """);

        run.CompilationErrors.ShouldBeEmpty();
        Json(run.Compilation).ShouldBe(Lf("""
            {
              "schemaVersion": 1,
              "project": "Subject",
              "requests": [
                {
                  "type": "Shop.Load<Shop.Order>",
                  "response": "Shop.Order",
                  "project": "Subject",
                  "handler": {
                    "type": "Shop.LoadHandler<Shop.Order>",
                    "project": "Subject"
                  },
                  "behaviors": [],
                  "preProcessors": [],
                  "postProcessors": [],
                  "exceptionActions": [],
                  "exceptionHandlers": []
                }
              ],
              "streams": [
                {
                  "type": "Shop.Tail",
                  "response": "string",
                  "project": "Subject",
                  "handler": {
                    "type": "Shop.TailHandler",
                    "project": "Subject"
                  },
                  "behaviors": [
                    {
                      "type": "Shop.Throttle<Shop.Tail, string>",
                      "project": "Subject"
                    }
                  ]
                }
              ],
              "notifications": [
                {
                  "type": "Shop.Placed",
                  "project": "Subject",
                  "handlers": [
                    {
                      "type": "Shop.Email",
                      "project": "Subject"
                    },
                    {
                      "type": "Shop.Ledger",
                      "project": "Subject"
                    }
                  ]
                }
              ],
              "unread": []
            }

            """));
    }

    [Fact]
    public void A_registration_the_build_cannot_read_is_listed_rather_than_guessed()
    {
        GeneratorRun run = GeneratorHarness.RunWithMessageMap(Handlers + """

            public static class Setup
            {
                public static IServiceCollection Add(IServiceCollection services, System.Type extra) =>
                    services.AddRelevox(options =>
                    {
                        options.AddOpenBehavior(extra);
                        Configure(options);
                        options.AddOpenBehavior(typeof(Logging<,>)).Extend();
                        options.BehaviorsToRegister.Clear();
                    });

                private static void Configure(RelevoxOptions options) { }

                private static void Extend(this RelevoxOptions options) { }
            }
            public sealed class Logging<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
            {
                public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken) => next(cancellationToken);
            }
            """);

        run.CompilationErrors.ShouldBeEmpty();
        Json(run.Compilation).ShouldNotBeNull().ShouldContain(Lf("""
              "unread": [
                "options.AddOpenBehavior(extra)",
                "Configure(options)",
                "options.AddOpenBehavior(typeof(Logging<,>)).Extend()",
                "options.BehaviorsToRegister.Clear()"
              ]
            """));
    }

    [Fact]
    public void The_map_reads_options_as_the_runtime_applies_them()
    {
        // Named arguments out of order, a behaviour added twice and kept once by TryAddEnumerable, and calls through
        // a null-conditional: the same map.
        GeneratorRun run = GeneratorHarness.RunWithMessageMap(Pipeline
            .Replace(
                "options.AddBehavior<LookCache>();",
                "options.AddBehavior(implementationType: typeof(LookCache), serviceType: typeof(IPipelineBehavior<Look, string>)); options.AddOpenBehavior(typeof(Logging<,>));")
            // Last, as the compiler takes options for maybe null after it.
            .Replace("options.AddRequestPreProcessor<Audit>();", "options?.AddRequestPreProcessor<Audit>();"));

        run.CompilationErrors.ShouldBeEmpty();
        Json(run.Compilation).ShouldBe(Json(GeneratorHarness.RunWithMessageMap(Pipeline).Compilation));
    }

    [Fact]
    public void A_method_of_the_same_name_on_something_else_is_not_an_options_call()
    {
        GeneratorRun run = GeneratorHarness.RunWithMessageMap(Pipeline.Replace(
            "options.AddBehavior<LookCache>();",
            "new Helper().AddBehavior(typeof(LookCache));") + """

            public sealed class Helper
            {
                public void AddBehavior(Type type) { }
            }
            """);

        run.CompilationErrors.ShouldBeEmpty();
        string map = Json(run.Compilation).ShouldNotBeNull();
        map.ShouldNotContain("Shop.LookCache");
        map.ShouldContain("\"unread\": []");
    }

    [Fact]
    public void A_flag_of_the_same_name_on_something_else_leaves_scanned_processors_off()
    {
        GeneratorRun run = GeneratorHarness.RunWithMessageMap(Pipeline
            .Replace("options.AutoRegisterRequestProcessors = true;", "new Flags().AutoRegisterRequestProcessors = true;")
            .Replace("options.AddRequestPreProcessor<Audit>();", "options.AddOpenRequestPreProcessor(typeof(Noop<>));") + """

            public sealed class Flags
            {
                public bool AutoRegisterRequestProcessors { get; set; }
            }
            public sealed class Noop<TRequest> : IRequestPreProcessor<TRequest> where TRequest : notnull
            {
                public Task Process(TRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """);

        run.CompilationErrors.ShouldBeEmpty();
        string map = Json(run.Compilation).ShouldNotBeNull();
        map.ShouldContain("Shop.Noop<Shop.Place>");
        map.ShouldNotContain("Shop.Audit");
    }

    [Fact]
    public void A_call_written_as_a_static_method_passes_no_options()
    {
        GeneratorRun run = GeneratorHarness.RunWithMessageMap(Handlers + """

            public static class Setup
            {
                public static IServiceCollection Add(IServiceCollection services) => RelevoxServiceCollectionExtensions.AddRelevox(services);
            }
            """);

        run.CompilationErrors.ShouldBeEmpty();
        Json(run.Compilation).ShouldNotBeNull().ShouldContain("\"unread\": []");
    }

    [Fact]
    public void Options_the_build_does_not_follow_are_listed_as_unread()
    {
        // An alias, and a lambda that may run later or never.
        GeneratorRun run = GeneratorHarness.RunWithMessageMap(Pipeline.Replace(
            "options.AddBehavior<LookCache>();",
            "var pipeline = options; pipeline.AddBehavior<LookCache>(); Action later = () => options.AddBehavior<LookCache>();"));

        run.CompilationErrors.ShouldBeEmpty();
        string map = Json(run.Compilation).ShouldNotBeNull();
        map.ShouldNotContain("Shop.LookCache");
        map.ShouldContain(Lf("""
              "unread": [
                "var pipeline = options;",
                "() => options.AddBehavior<LookCache>()"
              ]
            """));
    }

    [Fact]
    public void The_options_a_registration_returns_kept_in_a_variable_are_unread()
    {
        GeneratorRun run = GeneratorHarness.RunWithMessageMap(Pipeline.Replace(
            "options.AddBehavior<LookCache>();",
            "var pipeline = options.AddBehavior<LookCache>(); pipeline.AddRequestPreProcessor<Audit>();"));

        run.CompilationErrors.ShouldBeEmpty();
        string map = Json(run.Compilation).ShouldNotBeNull();
        map.ShouldContain(Lf("""
              "unread": [
                "var pipeline = options.AddBehavior<LookCache>();"
              ]
            """));
    }

    [Fact]
    public void A_registration_with_an_argument_RelevoxOptions_does_not_take_is_unread()
    {
        // The string picks the extension method, which registers nothing.
        GeneratorRun run = GeneratorHarness.RunWithMessageMap(Pipeline.Replace(
            "options.AddBehavior<LookCache>();",
            "options.AddBehavior<LookCache>(); options.AddOpenBehavior(typeof(Logging<,>), \"custom\");") + """

            public static class Tagged
            {
                public static RelevoxOptions AddOpenBehavior(this RelevoxOptions options, Type type, string tag) => options;
            }
            """);

        run.CompilationErrors.ShouldBeEmpty();
        string map = Json(run.Compilation).ShouldNotBeNull();
        map.ShouldContain(Lf("""
              "unread": [
                "options.AddOpenBehavior(typeof(Logging<,>), \"custom\")"
              ]
            """));
        map.Split("\"Shop.Logging<Shop.Place, int>\"").Length.ShouldBe(2, "only the chained registration puts it in");
    }

    [Fact]
    public void A_processor_type_in_a_variable_still_turns_its_stage_on()
    {
        // The scanned Audit runs because the call adds a pre-processor, whichever it is.
        GeneratorRun run = GeneratorHarness.RunWithMessageMap(Pipeline.Replace(
            "options.AddRequestPreProcessor<Audit>();",
            "Type audit = typeof(Audit); options.AddRequestPreProcessor(audit);"));

        run.CompilationErrors.ShouldBeEmpty();
        string map = Json(run.Compilation).ShouldNotBeNull();
        map.ShouldContain("\"Shop.Audit\"");
        map.ShouldContain("\"options.AddRequestPreProcessor(audit)\"");
    }

    [Fact]
    public void Another_librarys_AddRelevox_is_not_read()
    {
        GeneratorRun run = GeneratorHarness.RunWithMessageMap(Pipeline + """

            public static class Other
            {
                public static IServiceCollection AddRelevox(this IServiceCollection services, int retries) => services;
                public static IServiceCollection Add(IServiceCollection services) => services.AddRelevox(3);
            }
            """);

        run.CompilationErrors.ShouldBeEmpty();
        Json(run.Compilation).ShouldBe(Json(GeneratorHarness.RunWithMessageMap(Pipeline).Compilation));
    }

    [Fact]
    public void A_call_that_reaches_a_referenced_projects_AddRelevox_is_not_read()
    {
        // As in RVX0006: inside its own namespace the test project reaches its own AddRelevox; outside it, the app's.
        MetadataReference app = GeneratorHarness.Library("Shop.Api", [], withGenerator: true, """
            using Relevox; using System.Threading; using System.Threading.Tasks;
            [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Shop.Tests")]
            namespace Shop.Api;
            public sealed record Ping : IRequest<int>;
            internal sealed class PingHandler : IRequestHandler<Ping, int>
            {
                public Task<int> Handle(Ping request, CancellationToken cancellationToken) => Task.FromResult(1);
            }
            """);

        GeneratorRun run = GeneratorHarness.RunWithMessageMap("Shop.Tests", [app], """
            using Microsoft.Extensions.DependencyInjection; using Relevox; using System.Threading; using System.Threading.Tasks;
            namespace Shop.Tests
            {
                public sealed record Probe : IRequest<int>;
                public sealed class ProbeHandler : IRequestHandler<Probe, int>
                {
                    public Task<int> Handle(Probe request, CancellationToken cancellationToken) => Task.FromResult(2);
                }
                public sealed class Own<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
                {
                    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken) => next(cancellationToken);
                }
                public sealed class Theirs<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
                {
                    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken) => next(cancellationToken);
                }
                public static class Setup
                {
                    public static IServiceCollection Configure(IServiceCollection services) => services.AddRelevox(options => options.AddOpenBehavior(typeof(Own<,>)));
                }
            }
            public static class Outside
            {
                public static IServiceCollection Configure(IServiceCollection services) => services.AddRelevox(options => options.AddOpenBehavior(typeof(Shop.Tests.Theirs<,>)));
            }
            """);

        run.CompilationErrors.ShouldBeEmpty();
        string map = run.Compilation.GetTypeByMetadataName("Shop.Tests.RelevoxMessageMap")?.GetMembers("Json").OfType<IFieldSymbol>().Single().ConstantValue as string ?? "";
        map.ShouldContain("Shop.Tests.Own<Shop.Tests.Probe, int>");
        map.ShouldNotContain("Theirs");
        map.ShouldContain("\"unread\": []");
    }

    [Fact]
    public void With_two_AddRelevox_calls_the_options_are_listed_as_unread()
    {
        // The generated AddRelevox returns early the second time, so only one call's options ever apply.
        GeneratorRun run = GeneratorHarness.RunWithMessageMap(Pipeline + """

            public static class Again
            {
                public static IServiceCollection Add(IServiceCollection services) => services.AddRelevox();
            }
            """);

        run.CompilationErrors.ShouldBeEmpty();
        string map = Json(run.Compilation).ShouldNotBeNull();
        map.ShouldNotContain("Shop.Logging");
        map.ShouldContain("\"unread\": [\n    \"services.AddRelevox(options => { options.AutoRegisterRequestProcessors = true;");
    }

    [Fact]
    public void A_processor_registered_open_and_closed_runs_twice()
    {
        // The scan registers Noop<> open and the options add Noop<Place> closed: two descriptors, both resolved.
        GeneratorRun run = GeneratorHarness.RunWithMessageMap(Pipeline.Replace(
            "options.AddRequestPreProcessor<Audit>();",
            "options.AddRequestPreProcessor<Noop<Place>>();") + """

            public sealed class Noop<TRequest> : IRequestPreProcessor<TRequest> where TRequest : notnull
            {
                public Task Process(TRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """);

        run.CompilationErrors.ShouldBeEmpty();
        string map = Json(run.Compilation).ShouldNotBeNull();
        map.Split("\"Shop.Noop<Shop.Place>\"").Length.ShouldBe(3);
        map.Split("\"Shop.Noop<Shop.Look>\"").Length.ShouldBe(2, "on Look only the scan registers it");
    }

    [Fact]
    public void A_compound_assignment_to_the_processor_flag_is_unread()
    {
        GeneratorRun run = GeneratorHarness.RunWithMessageMap(Pipeline.Replace(
            "options.AddRequestPreProcessor<Audit>();",
            "options.AddRequestPreProcessor<Audit>(); options.AutoRegisterRequestProcessors |= false;"));

        run.CompilationErrors.ShouldBeEmpty();
        string map = Json(run.Compilation).ShouldNotBeNull();
        map.ShouldContain("\"options.AutoRegisterRequestProcessors |= false\"");
    }

    [Fact]
    public void A_later_false_turns_scanned_processors_back_off()
    {
        GeneratorRun run = GeneratorHarness.RunWithMessageMap(Pipeline.Replace(
            "options.AddRequestPreProcessor<Audit>();",
            "options.AddOpenRequestPreProcessor(typeof(Noop<>)); options.AutoRegisterRequestProcessors = false;") + """

            public sealed class Noop<TRequest> : IRequestPreProcessor<TRequest> where TRequest : notnull
            {
                public Task Process(TRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """);

        run.CompilationErrors.ShouldBeEmpty();
        string map = Json(run.Compilation).ShouldNotBeNull();
        map.ShouldContain("Shop.Noop<Shop.Place>");
        map.ShouldNotContain("Shop.Audit");
    }

    [Fact]
    public void Processors_are_left_out_unless_the_options_add_one_as_they_only_run_then()
    {
        GeneratorRun run = GeneratorHarness.RunWithMessageMap(Pipeline.Replace("options.AddRequestPreProcessor<Audit>();", ""));

        run.CompilationErrors.ShouldBeEmpty();
        Json(run.Compilation).ShouldNotBeNull().ShouldNotContain("Shop.Audit");
    }

    [Fact]
    public void An_abstract_request_is_left_out_as_the_mediator_leaves_it_out()
    {
        GeneratorRun run = GeneratorHarness.RunWithMessageMap(Handlers, """
            using Relevox; using System.Threading; using System.Threading.Tasks;
            namespace Shop;
            public abstract record Query : IRequest<int>;
            public sealed class QueryHandler : IRequestHandler<Query, int>
            {
                public Task<int> Handle(Query request, CancellationToken cancellationToken) => Task.FromResult(1);
            }
            """);

        run.CompilationErrors.ShouldBeEmpty();
        Json(run.Compilation).ShouldNotBeNull().ShouldNotContain("Shop.Query");
    }

    [Fact]
    public void With_the_switch_off_the_generated_code_is_what_it_was()
    {
        GeneratorRun off = GeneratorHarness.RunStrict(Pipeline);
        GeneratorRun on = GeneratorHarness.RunWithMessageMap(Pipeline);

        off.Sources.Keys.ShouldNotContain("RelevoxMessageMap.g.cs");
        on.Sources.Where(source => source.Key != "RelevoxMessageMap.g.cs").ShouldBe(off.Sources);
    }

    [Fact]
    public void A_project_using_another_projects_mediator_has_no_map()
    {
        MetadataReference application = GeneratorHarness.Library("Shop.Application", [], withGenerator: true, Handlers + """

            public static class Setup
            {
                public static IServiceCollection AddApplication(IServiceCollection services) => services.AddRelevox();
            }
            """);

        GeneratorRun run = GeneratorHarness.RunWithMessageMap("Shop.Api", [application], """
            public static class Program
            {
                public static void Configure(Microsoft.Extensions.DependencyInjection.IServiceCollection services) => Shop.Setup.AddApplication(services);
            }
            """);

        run.Sources.ShouldBeEmpty();
    }

    [Fact]
    public void An_edit_that_changes_no_message_leaves_the_map_alone()
    {
        var steps = GeneratorHarness.RunTwice(
            [Pipeline, "public static class Util { public static int One() => 1; }"],
            [Pipeline, "public static class Util { public static int One() => 2; }"],
            messageMap: true);

        steps[TrackingNames.MessageMap].ShouldAllBe(reason => reason == IncrementalStepRunReason.Cached || reason == IncrementalStepRunReason.Unchanged);
    }

    [Fact]
    public async Task Every_map_here_matches_the_published_schema()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Relevox.slnx")))
        {
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Relevox.slnx not found above the test output.");
        }

        NJsonSchema.JsonSchema schema = await NJsonSchema.JsonSchema.FromFileAsync(Path.Combine(root, "docs", "message-map.schema.json"), TestContext.Current.CancellationToken);
        string[] maps =
        [
            Json(GeneratorHarness.RunWithMessageMap(Pipeline).Compilation)!,
            Json(GeneratorHarness.RunWithMessageMap(Handlers).Compilation)!,
            Json(GeneratorHarness.RunWithMessageMap(
                Handlers,
                "using Microsoft.Extensions.DependencyInjection; public static class Setup { public static IServiceCollection Add(IServiceCollection s, System.Type t) => s.AddRelevox(o => o.AddOpenBehavior(t)); }").Compilation)!,
        ];

        foreach (string map in maps)
        {
            schema.Validate(map).ShouldBeEmpty(map);
        }
    }

    // What Relevox.Build reads: the constant's value, not the generated source text.
    private static string? Json(Compilation compilation) =>
        compilation.GetTypeByMetadataName("Subject.RelevoxMessageMap")?.GetMembers("Json").OfType<IFieldSymbol>().Single().ConstantValue as string;

    private static string Lf(string text) => text.ReplaceLineEndings("\n");
}
