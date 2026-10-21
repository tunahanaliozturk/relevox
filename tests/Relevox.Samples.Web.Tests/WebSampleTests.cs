using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Relevox.Samples.Web.Tests;

/// <summary>
/// The web sample over real HTTP, in process: each test gets a fresh application, so orders and activity start
/// empty.
/// </summary>
/// <remarks>
/// The point is the shape a web application gives the mediator: one container scope per request, handlers
/// and behaviours that depend on scoped services, exceptions turned into problem details, a streamed response.
/// None of the console samples or scenarios has those.
/// </remarks>
public sealed class WebSampleTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task An_order_placed_is_read_back_with_the_customer_from_the_scoped_service()
    {
        HttpClient client = Client("ada");

        HttpResponseMessage placed = await client.PostAsJsonAsync("/orders", new { product = "kettle", quantity = 2 });
        placed.StatusCode.ShouldBe(HttpStatusCode.Created);

        OrderBody? order = await client.GetFromJsonAsync<OrderBody>(placed.Headers.Location);
        order.ShouldBe(new OrderBody(1, "ada", "kettle", 2, false));
    }

    [Fact]
    public async Task The_validation_behaviour_answers_400_with_the_problems_per_field()
    {
        HttpResponseMessage response = await Client("ada").PostAsJsonAsync("/orders", new { product = "", quantity = 0 });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        Problem? problem = await response.Content.ReadFromJsonAsync<Problem>();
        problem!.Errors!.Keys.ShouldBe(["Product", "Quantity"], ignoreOrder: true);
    }

    [Fact]
    public async Task A_handler_exception_becomes_a_404()
    {
        HttpResponseMessage response = await Client("ada").GetAsync(new Uri("/orders/999", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Cancelling_publishes_to_both_notification_handlers_after_the_pre_processor()
    {
        HttpClient client = Client("bea");
        await client.PostAsJsonAsync("/orders", new { product = "lamp", quantity = 1 });

        HttpResponseMessage cancelled = await client.PostAsync(new Uri("/orders/1/cancel", UriKind.Relative), null);
        cancelled.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        string[]? activity = await client.GetFromJsonAsync<string[]>("/activity");
        activity.ShouldBe(
        [
            "bea sent PlaceOrder",
            "bea sent CancelOrder",
            "refund issued for order 1",
            "email sent to bea",
        ]);
    }

    [Fact]
    public async Task A_customer_streams_only_their_own_orders()
    {
        await Client("ada").PostAsJsonAsync("/orders", new { product = "kettle", quantity = 1 });
        await Client("bea").PostAsJsonAsync("/orders", new { product = "lamp", quantity = 1 });
        await Client("ada").PostAsJsonAsync("/orders", new { product = "mug", quantity = 3 });

        OrderBody[]? orders = await Client("ada").GetFromJsonAsync<OrderBody[]>("/orders");

        orders!.Select(order => order.Product).ShouldBe(["kettle", "mug"]);
    }

    [Fact]
    public async Task Concurrent_requests_each_see_their_own_scope()
    {
        string[] customers = [.. Enumerable.Range(0, 40).Select(index => $"customer{index}")];

        await Task.WhenAll(customers.Select(customer => Client(customer).PostAsJsonAsync("/orders", new { product = "pen", quantity = 1 })));

        foreach (string customer in customers)
        {
            OrderBody[]? orders = await Client(customer).GetFromJsonAsync<OrderBody[]>("/orders");
            orders!.ShouldHaveSingleItem().Customer.ShouldBe(customer);
        }
    }

    [Fact]
    public async Task With_telemetry_on_a_send_is_a_span_under_the_request_that_caused_it()
    {
        // Started spans rather than stopped ones: the request's own span can still be open when the response
        // arrives. Other tests run alongside, so every send recorded must sit under some request, not one in
        // particular.
        ConcurrentQueue<Activity> started = new();
        using ActivityListener listener = new()
        {
            ShouldListenTo = static source => source.Name is "Microsoft.AspNetCore" || source.Name == RelevoxTelemetry.SourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = started.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);

        HttpResponseMessage placed = await Client("ada").PostAsJsonAsync("/orders", new { product = "kettle", quantity = 2 });
        placed.StatusCode.ShouldBe(HttpStatusCode.Created);

        Activity[] sends = [.. started.Where(span => span.OperationName == "send PlaceOrder")];
        sends.ShouldNotBeEmpty();
        HashSet<ActivitySpanId> requests = [.. started.Where(span => span.Source.Name == "Microsoft.AspNetCore").Select(span => span.SpanId)];
        foreach (Activity send in sends)
        {
            requests.ShouldContain(send.ParentSpanId);
        }
    }

    private HttpClient Client(string customer)
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Customer", customer);
        return client;
    }

    private sealed record OrderBody(int Id, string Customer, string Product, int Quantity, bool Cancelled);

    private sealed record Problem(int Status, Dictionary<string, string[]>? Errors);
}
