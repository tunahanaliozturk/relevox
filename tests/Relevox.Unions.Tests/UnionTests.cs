using System.Text.Json;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Relevox.Testing;

namespace Relevox.Unions.Tests;

/// <summary>
/// C# 15 unions as responses, requests and notifications (#159), against the mediator generated for this project.
/// The generator treats a union like any other type; these pin what that gives an application.
/// </summary>
public sealed class UnionTests
{
    [Fact]
    public async Task A_union_response_comes_back_as_the_case_the_handler_returned()
    {
        await using ServiceProvider provider = Build();
        ISender sender = provider.GetRequiredService<ISender>();

        FindResult found = await sender.Send(new FindOrder(1));
        FindResult missing = await sender.Send(new FindOrder(0));

        Describe(found).ShouldBe("order 1");
        Describe(missing).ShouldBe("not found");
    }

    [Fact]
    public async Task A_union_response_sent_as_object_comes_back_as_the_union_and_not_its_case()
    {
        await using ServiceProvider provider = Build();

        object? response = await provider.GetRequiredService<ISender>().Send((object)new FindOrder(2));

        FindResult result = response.ShouldBeOfType<FindResult>();
        result.Value.ShouldBeOfType<Order>().Id.ShouldBe(2);
    }

    [Fact]
    public async Task A_behaviour_closed_over_a_union_response_runs_and_can_switch_on_it()
    {
        await using ServiceProvider provider = Build(static options => options.AddBehavior<IPipelineBehavior<FindOrder, FindResult>, FindOrderAudit>());
        ISender sender = provider.GetRequiredService<ISender>();

        await sender.Send(new FindOrder(3));
        await sender.Send(new FindOrder(0));

        provider.GetRequiredService<Trace>().Events.ShouldBe(["audit: order 3", "audit: not found"]);
    }

    [Fact]
    public async Task Each_item_of_a_stream_of_unions_arrives_as_the_union()
    {
        await using ServiceProvider provider = Build();
        List<string> items = [];

        await foreach (FindResult item in provider.GetRequiredService<ISender>().CreateStream(new ListOrders(2)))
        {
            items.Add(Describe(item));
        }

        items.ShouldBe(["order 1", "order 2", "not found"]);

        List<object?> untyped = [];
        await foreach (object? item in provider.GetRequiredService<ISender>().CreateStream((object)new ListOrders(1)))
        {
            untyped.Add(item);
        }

        untyped.Select(static item => item?.GetType()).ShouldBe([typeof(FindResult), typeof(FindResult)]);
    }

    [Fact]
    public async Task A_union_notification_reaches_the_unions_handlers()
    {
        await using ServiceProvider provider = Build();
        OrderEvent placed = new Placed(4);

        await provider.GetRequiredService<IPublisher>().Publish(placed);

        provider.GetRequiredService<Trace>().Events.ShouldBe(["event: placed 4"]);
    }

    [Fact]
    public async Task A_union_published_as_object_reaches_the_unions_handlers_and_not_those_of_its_case()
    {
        await using ServiceProvider provider = Build();
        IPublisher publisher = provider.GetRequiredService<IPublisher>();
        OrderEvent cancelled = new Cancelled(5);

        await publisher.Publish((object)cancelled);
        await publisher.Publish(cancelled);

        // Dispatch goes by the published type. The union is a type of its own, not the case it holds.
        provider.GetRequiredService<Trace>().Events.ShouldBe(["event: cancelled 5", "event: cancelled 5"]);

        // Published as the case, the case's handler runs and the union's does not.
        await publisher.Publish(new Cancelled(6));
        provider.GetRequiredService<Trace>().Events[^1].ShouldBe("cancelled 6");
        provider.GetRequiredService<Trace>().Events.Count.ShouldBe(3);
    }

    [Fact]
    public async Task A_union_request_reaches_its_handler()
    {
        await using ServiceProvider provider = Build();
        ISender sender = provider.GetRequiredService<ISender>();

        (await sender.Send(new Command(new Open(7)))).ShouldBe("opened 7");
        (await sender.Send((object)new Command(new Close(8)))).ShouldBeOfType<string>().ShouldBe("closed 8");
    }

    [Fact]
    public async Task The_validation_behaviour_is_closed_over_a_union_request_and_validates_it()
    {
        await using ServiceProvider provider = Build(static options => options.AddValidation());
        ISender sender = provider.GetRequiredService<ISender>();

        (await sender.Send(new Command(new Close(9)))).ShouldBe("closed 9");
        ValidationException exception = await Should.ThrowAsync<ValidationException>(() => sender.Send(new Command(new Close(0))));

        exception.Errors.ShouldHaveSingleItem().ErrorMessage.ShouldBe("Only an existing order can be closed.");
    }

    [Fact]
    public async Task A_generic_union_closed_in_a_response_comes_back_as_its_case()
    {
        await using ServiceProvider provider = Build();
        ISender sender = provider.GetRequiredService<ISender>();

        Lookup<string> found = await sender.Send(new FindName(1));
        Lookup<string> missing = await sender.Send(new FindName(2));

        found.Value.ShouldBeOfType<string>().ShouldBe("Ada");
        missing.Value.ShouldBeOfType<NotFound>();
        (await sender.Send((object)new FindName(1))).ShouldBeOfType<Lookup<string>>();
    }

    [Fact]
    public async Task The_fake_mediator_answers_with_a_union_and_records_a_published_union()
    {
        FakeMediator mediator = new FakeMediator().Setup<FindOrder, FindResult>(static query => new Order(query.Id));
        OrderEvent placed = new Placed(10);

        FindResult result = await mediator.Send(new FindOrder(10));
        await mediator.Publish(placed);

        Describe(result).ShouldBe("order 10");
        mediator.Sent<FindOrder>().ShouldBe([new FindOrder(10)]);
        mediator.Published<OrderEvent>().ShouldHaveSingleItem().Value.ShouldBe(new Placed(10));
        mediator.Published<Placed>().ShouldBeEmpty();
    }

    [Fact]
    public async Task The_notification_recorder_records_the_union_and_not_its_case()
    {
        ServiceCollection services = Services(static _ => { });
        services.AddRelevoxNotificationRecorder();
        await using ServiceProvider provider = services.BuildServiceProvider();
        OrderEvent cancelled = new Cancelled(11);

        await provider.GetRequiredService<IPublisher>().Publish(cancelled);

        NotificationRecorder recorder = provider.GetRequiredService<NotificationRecorder>();
        recorder.Published<OrderEvent>().ShouldHaveSingleItem().Value.ShouldBe(new Cancelled(11));
        recorder.Published<Cancelled>().ShouldBeEmpty();
        provider.GetRequiredService<Trace>().Events.ShouldBe(["event: cancelled 11"]);
    }

    [Fact]
    public void The_message_map_names_the_union_as_the_response_and_as_the_notification()
    {
        using JsonDocument map = JsonDocument.Parse(RelevoxMessageMap.Json);
        JsonElement root = map.RootElement;

        Entry(root, "requests", "Relevox.Unions.Tests.FindOrder").GetProperty("response").GetString().ShouldBe("Relevox.Unions.Tests.FindResult");
        Entry(root, "requests", "Relevox.Unions.Tests.FindName").GetProperty("response").GetString().ShouldBe("Relevox.Unions.Tests.Lookup<string>");
        Entry(root, "requests", "Relevox.Unions.Tests.Command").GetProperty("handler").GetProperty("type").GetString().ShouldBe("Relevox.Unions.Tests.CommandHandler");
        Entry(root, "streams", "Relevox.Unions.Tests.ListOrders").GetProperty("response").GetString().ShouldBe("Relevox.Unions.Tests.FindResult");

        // Each notification lists its own handlers: the union's are not listed under its cases, nor the other way round.
        Handlers(root, "Relevox.Unions.Tests.OrderEvent").ShouldBe(["Relevox.Unions.Tests.OnOrderEvent"]);
        Handlers(root, "Relevox.Unions.Tests.Cancelled").ShouldBe(["Relevox.Unions.Tests.OnCancelled"]);
    }

    // No discard arm: the switch is exhaustive over the union's cases, and warnings are errors, so a response type
    // the compiler no longer saw as this union would fail the build here.
    private static string Describe(FindResult result) => result switch
    {
        Order order => $"order {order.Id}",
        NotFound => "not found",
    };

    private static JsonElement Entry(JsonElement map, string section, string type) =>
        map.GetProperty(section).EnumerateArray().Single(entry => entry.GetProperty("type").GetString() == type);

    private static string?[] Handlers(JsonElement map, string notification) =>
        [.. Entry(map, "notifications", notification).GetProperty("handlers").EnumerateArray().Select(static handler => handler.GetProperty("type").GetString())];

    private static ServiceProvider Build(Action<RelevoxOptions>? configure = null) =>
        Services(configure ?? (static _ => { })).BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

    private static ServiceCollection Services(Action<RelevoxOptions> configure)
    {
        ServiceCollection services = new();
        services.AddSingleton<Trace>();
        services.AddTransient<IValidator<Command>, CommandValidator>();
        services.AddRelevox(configure);
        return services;
    }
}
