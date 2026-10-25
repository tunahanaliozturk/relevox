using System.Diagnostics;
using System.Text.Json;
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

    // A generic request closed at build time, notifications with a handler for an interface, and a stream with an
    // open behaviour.
    private const string Messages = """
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
        """;

    // Calls to Handlers' requests and a notification: directly, through each interface, in a lambda and a local
    // function, chained over two lines, and one passing object, which the map leaves out. File1.cs after Handlers.
    private const string Callers = """
        using System; using System.Threading; using System.Threading.Tasks; using Relevox;
        namespace Shop;
        public sealed record Shipped : INotification;
        public sealed class Ship : INotificationHandler<Shipped> { public Task Handle(Shipped notification, CancellationToken cancellationToken) => Task.CompletedTask; }
        public sealed class Callers(ISender sender, IPublisher publisher, IMediator mediator)
        {
            public Task<string> Direct() => sender.Send(new Ping("a"));
            public Task Published() => publisher.Publish(new Shipped());
            public Func<Task> Later() => () => mediator.Send(new Touch());
            public Task Local()
            {
                return Run();
                Task Run() => mediator
                    .Send(new Ping("b"));
            }
            public Task<object?> Untyped() => sender.Send((object)new Ping("c"));
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
                  "exceptionHandlers": [],
                  "callSites": []
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
                  ],
                  "callSites": []
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
        GeneratorRun run = GeneratorHarness.RunWithMessageMap(Messages);

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
                  "exceptionHandlers": [],
                  "callSites": []
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
                  ],
                  "callSites": []
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
                  ],
                  "callSites": []
                }
              ],
              "unread": []
            }

            """));
    }

    [Fact]
    public void The_markdown_view_has_a_table_per_request_with_its_pipeline_in_order()
    {
        GeneratorRun run = GeneratorHarness.RunWithMessageMap(Pipeline);

        run.CompilationErrors.ShouldBeEmpty();
        Markdown(run.Compilation).ShouldBe(Lf("""
            # Message map of Subject

            Written by the build from what Relevox's generator sees; the same map as JSON is in the file next to this one. Each table lists a message's pipeline from top to bottom in the order it runs. Behaviours wrap the handler, outermost first; exception handlers and actions wrap the whole pipeline and run only when it throws. They are listed as they run with the default RequestExceptionActionProcessorStrategy, handlers first; with ApplyForAllExceptions the actions run first.

            ## Requests

            ### `Shop.Look`

            Returns `string`. Declared in Subject.

            | Step | Type | Project |
            |---|---|---|
            | Behaviour | `Shop.Logging<Shop.Look, string>` | Subject |
            | Behaviour | `Shop.LookCache` | Subject |
            | Handler | `Shop.LookHandler` | Subject |

            ### `Shop.Place`

            Returns `int`. Declared in Subject.

            | Step | Type | Project |
            |---|---|---|
            | Pre-processor | `Shop.Audit` | Subject |
            | Behaviour | `Shop.Logging<Shop.Place, int>` | Subject |
            | Behaviour | `Shop.Transaction<Shop.Place, int>` | Subject |
            | Handler | `Shop.PlaceHandler` | Subject |
            | Exception handler, `System.InvalidOperationException` | `Shop.Refused` | Subject |

            """));
    }

    // The pipeline above with PipelineOrder on two behaviours (#97): Logging moves inside LookCache, and Transaction
    // outside the pre-processor.
    private static readonly string Ordered = Pipeline
        .Replace("public sealed class Logging<", "[PipelineOrder(1)] public sealed class Logging<", StringComparison.Ordinal)
        .Replace("public sealed class Transaction<", "[PipelineOrder(-5)] public sealed class Transaction<", StringComparison.Ordinal);

    [Fact]
    public void Behaviours_are_listed_in_the_order_PipelineOrder_gives_with_their_order()
    {
        GeneratorRun run = GeneratorHarness.RunWithMessageMap(Ordered);

        run.CompilationErrors.ShouldBeEmpty();
        Json(run.Compilation)!.ShouldContain(Lf("""
                  "behaviors": [
                    {
                      "type": "Shop.LookCache",
                      "project": "Subject"
                    },
                    {
                      "type": "Shop.Logging<Shop.Look, string>",
                      "project": "Subject",
                      "order": 1
                    }
                  ],
            """));
        Markdown(run.Compilation).ShouldBe(Lf("""
            # Message map of Subject

            Written by the build from what Relevox's generator sees; the same map as JSON is in the file next to this one. Each table lists a message's pipeline from top to bottom in the order it runs. Behaviours wrap the handler, outermost first; exception handlers and actions wrap the whole pipeline and run only when it throws. They are listed as they run with the default RequestExceptionActionProcessorStrategy, handlers first; with ApplyForAllExceptions the actions run first. A behaviour ordered below zero runs outside all of them, the processors included.

            ## Requests

            ### `Shop.Look`

            Returns `string`. Declared in Subject.

            | Step | Type | Project |
            |---|---|---|
            | Behaviour | `Shop.LookCache` | Subject |
            | Behaviour, order 1 | `Shop.Logging<Shop.Look, string>` | Subject |
            | Handler | `Shop.LookHandler` | Subject |

            ### `Shop.Place`

            Returns `int`. Declared in Subject.

            | Step | Type | Project |
            |---|---|---|
            | Behaviour, order -5 | `Shop.Transaction<Shop.Place, int>` | Subject |
            | Pre-processor | `Shop.Audit` | Subject |
            | Behaviour, order 1 | `Shop.Logging<Shop.Place, int>` | Subject |
            | Handler | `Shop.PlaceHandler` | Subject |
            | Exception handler, `System.InvalidOperationException` | `Shop.Refused` | Subject |

            """));
    }

    [Fact]
    public void The_markdown_view_draws_each_notification_with_its_handlers_in_order()
    {
        GeneratorRun run = GeneratorHarness.RunWithMessageMap(Messages);

        run.CompilationErrors.ShouldBeEmpty();
        Markdown(run.Compilation).ShouldBe(Lf("""
            # Message map of Subject

            Written by the build from what Relevox's generator sees; the same map as JSON is in the file next to this one. Each table lists a message's pipeline from top to bottom in the order it runs. Behaviours wrap the handler, outermost first; exception handlers and actions wrap the whole pipeline and run only when it throws. They are listed as they run with the default RequestExceptionActionProcessorStrategy, handlers first; with ApplyForAllExceptions the actions run first.

            ## Requests

            ### `Shop.Load<Shop.Order>`

            Returns `Shop.Order`. Declared in Subject.

            | Step | Type | Project |
            |---|---|---|
            | Handler | `Shop.LoadHandler<Shop.Order>` | Subject |

            ## Streams

            ### `Shop.Tail`

            Yields `string`. Declared in Subject.

            | Step | Type | Project |
            |---|---|---|
            | Behaviour | `Shop.Throttle<Shop.Tail, string>` | Subject |
            | Handler | `Shop.TailHandler` | Subject |

            ## Notifications

            ### `Shop.Placed`

            Declared in Subject.

            ```mermaid
            flowchart LR
              notification["Shop.Placed"]
              notification --> handler1["1. Shop.Email"]
              notification --> handler2["2. Shop.Ledger"]
            ```

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
        Markdown(run.Compilation).ShouldNotBeNull().ShouldEndWith(Lf("""
            ## Not read

            The build could not read these registrations, so what they add is missing above:

            - `options.AddOpenBehavior(extra)`
            - `Configure(options)`
            - `options.AddOpenBehavior(typeof(Logging<,>)).Extend()`
            - `options.BehaviorsToRegister.Clear()`

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
        map.ShouldContain("\"options.AddOpenBehavior(typeof(Logging<,>), \\\"custom\\\")\"");
        map.ShouldNotContain("Shop.Logging", customMessage: "with the extension in scope, no AddOpenBehavior call is read");
    }

    [Fact]
    public void A_registration_an_extension_of_the_same_name_may_take_is_unread()
    {
        // Only types go in, and the extension method still wins over the options' own one.
        GeneratorRun run = GeneratorHarness.RunWithMessageMap(Pipeline.Replace(
            "options.AddBehavior<LookCache>();",
            "options.AddBehavior<LookCache>(typeof(string));") + """

            public static class Typed
            {
                public static RelevoxOptions AddBehavior<T>(this RelevoxOptions options, Type type) => options;
            }
            """);

        run.CompilationErrors.ShouldBeEmpty();
        string map = Json(run.Compilation).ShouldNotBeNull();
        map.ShouldNotContain("Shop.LookCache");
        map.ShouldContain("\"options.AddBehavior<LookCache>(typeof(string))\"");
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
    public void Backticks_in_unread_source_stay_inside_their_code_span()
    {
        GeneratorRun run = GeneratorHarness.RunWithMessageMap(Pipeline.Replace(
            "options.AddBehavior<LookCache>();",
            "Extra.Configure(options, \"``\");") + """

            public static class Extra
            {
                public static void Configure(RelevoxOptions options, string tag) { }
            }
            """);

        run.CompilationErrors.ShouldBeEmpty();
        Markdown(run.Compilation).ShouldNotBeNull().ShouldContain("``` Extra.Configure(options, \"``\") ```");
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
    public void A_processor_flag_set_in_a_branch_is_unread()
    {
        GeneratorRun run = GeneratorHarness.RunWithMessageMap(Pipeline.Replace(
            "options.AutoRegisterRequestProcessors = true;",
            "if (DateTime.Now.Year > 2000) options.AutoRegisterRequestProcessors = true;"));

        run.CompilationErrors.ShouldBeEmpty();
        Json(run.Compilation).ShouldNotBeNull().ShouldContain("\"options.AutoRegisterRequestProcessors = true\"");
    }

    [Fact]
    public void A_descriptor_added_to_a_processor_list_turns_its_stage_on()
    {
        // The scanned Audit runs because the list is no longer empty.
        GeneratorRun run = GeneratorHarness.RunWithMessageMap(Pipeline.Replace(
            "options.AddRequestPreProcessor<Audit>();",
            "options.RequestPreProcessorsToRegister.Add(ServiceDescriptor.Transient(typeof(IRequestPreProcessor<Place>), typeof(Audit)));"));

        run.CompilationErrors.ShouldBeEmpty();
        string map = Json(run.Compilation).ShouldNotBeNull();
        map.ShouldContain("\"Shop.Audit\"");
        map.ShouldContain("\"options.RequestPreProcessorsToRegister.Add(");
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
    public void A_project_using_another_projects_mediator_lists_the_calls_it_makes()
    {
        MetadataReference application = GeneratorHarness.Library("Shop.Application", [], withGenerator: true, Handlers, Setup);

        // A message held as its interface is left out, as in a mediator's own map.
        GeneratorRun run = GeneratorHarness.RunWithMessageMap("Shop.Web", [application], """
            using Relevox; using System.Threading.Tasks;
            public sealed class Endpoints(ISender sender)
            {
                public Task<string> Ping() => sender.Send(new Shop.Ping("a"));
                public Task Touch() => sender.Send(new Shop.Touch());
                public Task<string> Again() => sender.Send(new Shop.Ping("b"));
                public Task<string> Held(IRequest<string> request) => sender.Send(request);
            }
            """);

        run.CompilationErrors.ShouldBeEmpty();
        run.Sources.Keys.ShouldHaveSingleItem().ShouldBe("RelevoxMessageMap.g.cs");
        Json(run.Compilation, "Shop.Web").ShouldBe(Lf("""
            {
              "schemaVersion": 1,
              "project": "Shop.Web",
              "requests": [],
              "streams": [],
              "notifications": [],
              "unread": [],
              "calls": [
                {
                  "type": "Shop.Ping",
                  "callSites": [
                    {
                      "file": "File0.cs",
                      "line": 4
                    },
                    {
                      "file": "File0.cs",
                      "line": 6
                    }
                  ]
                },
                {
                  "type": "Shop.Touch",
                  "callSites": [
                    {
                      "file": "File0.cs",
                      "line": 5
                    }
                  ]
                }
              ]
            }

            """));
        Markdown(run.Compilation, "Shop.Web").ShouldNotBeNull().ShouldEndWith(Lf("""
            ## Calls

            ### `Shop.Ping`

            Called from `File0.cs:4`, `File0.cs:6`.

            ### `Shop.Touch`

            Called from `File0.cs:5`.

            """));
    }

    [Fact]
    public void The_combined_view_gathers_every_projects_calls_and_finds_the_requests_nothing_sends()
    {
        // One request sent by the mediator's own project, one by a consumer, one by two consumers, one by nobody.
        const string requests = """
            using Relevox; using System.Threading; using System.Threading.Tasks;
            namespace Shop;
            public sealed record Own : IRequest;
            public sealed record Sent : IRequest;
            public sealed record Shared : IRequest;
            public sealed record Unsent : IRequest;
            public sealed class Handlers : IRequestHandler<Own>, IRequestHandler<Sent>, IRequestHandler<Shared>, IRequestHandler<Unsent>
            {
                public Task Handle(Own request, CancellationToken cancellationToken) => Task.CompletedTask;
                public Task Handle(Sent request, CancellationToken cancellationToken) => Task.CompletedTask;
                public Task Handle(Shared request, CancellationToken cancellationToken) => Task.CompletedTask;
                public Task Handle(Unsent request, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            public sealed class Inside(ISender sender) { public Task Go() => sender.Send(new Own()); }
            """;
        MetadataReference application = GeneratorHarness.Library("Shop.Application", [], withGenerator: true, requests, Setup);
        string directory = Directory.CreateTempSubdirectory("relevox-maps-").FullName;
        try
        {
            string[] files =
            [
                Save("application", Json(GeneratorHarness.RunWithMessageMap("Shop.Application", [], requests, Setup).Compilation, "Shop.Application")),
                Save("web", Consumer("Shop.Web", "sender.Send(new Shop.Sent()); sender.Send(new Shop.Shared());")),
                Save("jobs", Consumer("Shop.Jobs", "sender.Send(new Shop.Shared());")),
            ];

            using JsonDocument combined = JsonDocument.Parse(Jq(["--slurp", "--from-file", Path.Combine(RepositoryRoot(), "docs", "message-map-combine.jq"), .. files]));
            JsonElement mediator = combined.RootElement.EnumerateArray().ShouldHaveSingleItem();
            mediator.GetProperty("project").GetString().ShouldBe("Shop.Application");
            Dictionary<string, string[]> sentFrom = mediator.GetProperty("requests").EnumerateArray().ToDictionary(
                static request => request.GetProperty("type").GetString()!,
                static request => request.GetProperty("callSites").EnumerateArray().Select(static site => $"{site.GetProperty("project")} {site.GetProperty("file")}:{site.GetProperty("line")}").ToArray());
            sentFrom["Shop.Own"].ShouldBe(["Shop.Application File0.cs:14"]);
            sentFrom["Shop.Sent"].ShouldBe(["Shop.Web File0.cs:1"]);
            sentFrom["Shop.Shared"].ShouldBe(["Shop.Jobs File0.cs:1", "Shop.Web File0.cs:1"]);
            sentFrom["Shop.Unsent"].ShouldBeEmpty();

            // The check docs/message-map.md gives, as written there.
            string combinedFile = Save("combined", combined.RootElement.GetRawText());
            Jq(["-r", ".[].requests[] | select(.callSites == []) | .type", combinedFile]).ShouldBe(Lf("Shop.Unsent\n"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        string Consumer(string name, string calls) => Json(GeneratorHarness.RunWithMessageMap(name, [application], $$"""
            public sealed class Calls(Relevox.ISender sender) { public void Go() { {{calls}} } }
            """).Compilation, name)!;

        string Save(string name, string? content)
        {
            string file = Path.Combine(directory, $"{name}.json");
            File.WriteAllText(file, content.ShouldNotBeNull());
            return file;
        }
    }

    [Fact]
    public void Each_message_lists_where_it_is_sent_from()
    {
        GeneratorRun run = GeneratorHarness.RunWithMessageMap(Handlers, Callers);

        run.CompilationErrors.ShouldBeEmpty();
        string markdown = Markdown(run.Compilation).ShouldNotBeNull();
        markdown.ShouldContain(Lf("""
            ### `Shop.Ping`

            Returns `string`. Declared in Subject.

            Sent from `File1.cs:7`, `File1.cs:14`.
            """));
        markdown.ShouldContain("Sent from `File1.cs:9`.");
        markdown.ShouldContain("Published from `File1.cs:8`.");
        Json(run.Compilation).ShouldNotBeNull().ShouldNotContain("\"line\": 16");
    }

    [Fact]
    public void A_call_on_the_generated_mediator_is_a_call_site()
    {
        // RelevoxMediator does not exist yet when the generator reads the call. File2.cs, after Handlers and Callers.
        GeneratorRun run = GeneratorHarness.RunWithMessageMap(Handlers, Callers, """
            namespace Shop;
            internal sealed class Typed(Subject.RelevoxMediator mediator) { public System.Threading.Tasks.Task<string> Go() => mediator.Send(new Ping("t"));
                public System.Threading.Tasks.Task<string> Named() => mediator.Send(cancellationToken: default, request: new Ping("n")); }
            """);

        run.CompilationErrors.ShouldBeEmpty();
        Markdown(run.Compilation).ShouldNotBeNull().ShouldContain("Sent from `File1.cs:7`, `File1.cs:14`, `File2.cs:2`, `File2.cs:3`.");
    }

    [Fact]
    public void A_call_site_is_written_relative_to_the_project()
    {
        CallSites.Relative(@"C:\src\Shop\Orders\Endpoints.cs", @"C:\src\Shop\").ShouldBe("Orders/Endpoints.cs");
        CallSites.Relative("/src/shop/Orders/Endpoints.cs", "/src/shop/").ShouldBe("Orders/Endpoints.cs");
        CallSites.Relative(@"C:\elsewhere\Linked.cs", @"C:\src\Shop\").ShouldBe("Linked.cs");
    }

    [Fact]
    public void An_edit_that_adds_no_call_site_leaves_the_map_alone()
    {
        var steps = GeneratorHarness.RunTwice(
            [Handlers, Callers, "public static class Util { public static int One() => 1; }"],
            [Handlers, Callers, "public static class Util { public static int One() => 2; }"],
            messageMap: true);

        steps[TrackingNames.MessageMap].ShouldAllBe(reason => reason == IncrementalStepRunReason.Cached || reason == IncrementalStepRunReason.Unchanged);
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
        NJsonSchema.JsonSchema schema = await NJsonSchema.JsonSchema.FromFileAsync(Path.Combine(RepositoryRoot(), "docs", "message-map.schema.json"), TestContext.Current.CancellationToken);
        MetadataReference application = GeneratorHarness.Library("Shop.Application", [], withGenerator: true, Handlers, Setup);
        string[] maps =
        [
            Json(GeneratorHarness.RunWithMessageMap(Pipeline).Compilation)!,
            Json(GeneratorHarness.RunWithMessageMap(Ordered).Compilation)!,
            Json(GeneratorHarness.RunWithMessageMap(Handlers).Compilation)!,
            Json(GeneratorHarness.RunWithMessageMap(Handlers, Callers).Compilation)!,
            Json(GeneratorHarness.RunWithMessageMap(
                Handlers,
                "using Microsoft.Extensions.DependencyInjection; public static class Setup { public static IServiceCollection Add(IServiceCollection s, System.Type t) => s.AddRelevox(o => o.AddOpenBehavior(t)); }").Compilation)!,
            Json(GeneratorHarness.RunWithMessageMap("Shop.Web", [application], "public sealed class Calls(Relevox.ISender sender) { public void Go() => sender.Send(new Shop.Touch()); }").Compilation, "Shop.Web")!,
        ];

        foreach (string map in maps)
        {
            schema.Validate(map).ShouldBeEmpty(map);
        }
    }

    private const string Setup = """
        using Microsoft.Extensions.DependencyInjection;
        namespace Shop;
        public static class Setup { public static IServiceCollection AddApplication(IServiceCollection services) => services.AddRelevox(); }
        """;

    // What Relevox.Build reads: the constant's value, not the generated source text.
    private static string? Json(Compilation compilation, string ns = "Subject") =>
        compilation.GetTypeByMetadataName($"{ns}.RelevoxMessageMap")?.GetMembers("Json").OfType<IFieldSymbol>().Single().ConstantValue as string;

    private static string? Markdown(Compilation compilation, string ns = "Subject") =>
        compilation.GetTypeByMetadataName($"{ns}.RelevoxMessageMap")?.GetMembers("Markdown").OfType<IFieldSymbol>().Single().ConstantValue as string;

    private static string RepositoryRoot()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Relevox.slnx")))
        {
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Relevox.slnx not found above the test output.");
        }

        return root;
    }

    // jq runs the recipe in docs/message-map.md. CI's runners have it; a machine without it skips the test.
    private static string Jq(string[] arguments)
    {
        ProcessStartInfo start = new("jq") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        Process jq;
        try
        {
            jq = Process.Start(start)!;
        }
        catch (System.ComponentModel.Win32Exception) when (Environment.GetEnvironmentVariable("CI") is null)
        {
            Assert.Skip("jq is not installed.");
            throw;
        }

        using (jq)
        {
            string output = jq.StandardOutput.ReadToEnd();
            string error = jq.StandardError.ReadToEnd();
            jq.WaitForExit();
            jq.ExitCode.ShouldBe(0, error);
            return output.ReplaceLineEndings("\n");
        }
    }

    private static string Lf(string text) => text.ReplaceLineEndings("\n");
}
