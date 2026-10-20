namespace Relevox.Testing.Tests;

public sealed class FakeMediatorTests
{
    [Fact]
    public async Task A_set_up_request_is_answered_and_recorded()
    {
        FakeMediator mediator = new FakeMediator().Setup((GetPrice query) => query.Sku == "bolt" ? 1.25m : 0m);

        (await mediator.Send(new GetPrice("bolt"))).ShouldBe(1.25m);
        (await mediator.Send(new GetPrice("nut"))).ShouldBe(0m);

        mediator.Sent<GetPrice>().ShouldBe([new GetPrice("bolt"), new GetPrice("nut")]);
    }

    [Fact]
    public async Task A_request_nobody_set_up_fails_with_its_name()
    {
        // A default answer would let the code under test carry on with a price of zero.
        FakeMediator mediator = new();

        Task<decimal> send = mediator.Send(new GetPrice("bolt"));

        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(send);
        exception.Message.ShouldContain(typeof(GetPrice).FullName!);
        mediator.Sent<object>().ShouldBe([new GetPrice("bolt")]);
    }

    [Fact]
    public async Task A_request_without_a_response_runs_its_setup_through_every_send()
    {
        List<string> archived = [];
        FakeMediator mediator = new FakeMediator().Setup((Archive command) => { archived.Add(command.Sku); });

        await mediator.Send(new Archive("bolt"));
        (await mediator.Send((object)new Archive("nut"))).ShouldBe(Unit.Value);

        archived.ShouldBe(["bolt", "nut"]);
    }

    [Fact]
    public async Task An_async_setup_gets_the_sends_cancellation_token()
    {
        using CancellationTokenSource source = new();
        CancellationToken seen = default;
        FakeMediator mediator = new FakeMediator().SetupAsync((GetPrice _, CancellationToken token) =>
        {
            seen = token;
            return Task.FromResult(2m);
        });

        (await mediator.Send(new GetPrice("bolt"), source.Token)).ShouldBe(2m);
        seen.ShouldBe(source.Token);
    }

    [Fact]
    public async Task A_failing_setup_faults_the_task_rather_than_throwing_from_send()
    {
        FakeMediator mediator = new FakeMediator()
            .Throws<GetPrice>(new TimeoutException("pricing is down"))
            .Setup((Archive _) => throw new InvalidOperationException("archive is full"));

        Task<decimal> price = mediator.Send(new GetPrice("bolt"));
        Task archive = mediator.Send(new Archive("bolt"));

        (await Should.ThrowAsync<TimeoutException>(price)).Message.ShouldBe("pricing is down");
        (await Should.ThrowAsync<InvalidOperationException>(archive)).Message.ShouldBe("archive is full");
    }

    [Fact]
    public async Task A_stream_yields_its_setup_and_one_nobody_set_up_fails_when_enumerated()
    {
        FakeMediator mediator = new FakeMediator().SetupStream((ListSkus query) => Enumerable.Range(1, query.Count).Select(index => $"sku-{index}"));

        List<string> skus = [];
        await foreach (string sku in mediator.CreateStream(new ListSkus(3)))
        {
            skus.Add(sku);
        }

        skus.ShouldBe(["sku-1", "sku-2", "sku-3"]);

        IAsyncEnumerable<string> missing = new FakeMediator().CreateStream(new ListSkus(1));
        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (string _ in missing)
            {
            }
        });
    }

    [Fact]
    public async Task A_stream_setup_runs_when_enumeration_starts_not_when_the_stream_is_created()
    {
        int calls = 0;
        FakeMediator mediator = new FakeMediator().SetupStream((ListSkus _) =>
        {
            calls++;
            return Enumerable.Repeat("a", 1);
        });

        IAsyncEnumerable<string> stream = mediator.CreateStream(new ListSkus(1));
        calls.ShouldBe(0);

        await foreach (string _ in stream)
        {
        }

        calls.ShouldBe(1);
    }

    [Fact]
    public async Task A_typed_stream_stops_when_the_token_given_to_WithCancellation_is_cancelled()
    {
        using CancellationTokenSource source = new();
        FakeMediator mediator = new FakeMediator().SetupStream((ListSkus query) => Enumerable.Range(1, query.Count).Select(index => $"sku-{index}"));

        List<string> read = [];
        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (string sku in mediator.CreateStream(new ListSkus(10)).WithCancellation(source.Token))
            {
                read.Add(sku);
                if (read.Count == 2)
                {
                    await source.CancelAsync();
                }
            }
        });

        read.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Published_notifications_are_recorded_by_type()
    {
        FakeMediator mediator = new();

        await mediator.Publish(new PriceChanged("bolt", 1.25m));
        await mediator.Publish((object)new Unheard("nut"));

        mediator.Published<PriceChanged>().ShouldBe([new PriceChanged("bolt", 1.25m)]);
        mediator.Published<object>().Count.ShouldBe(2);

        mediator.Clear();
        mediator.Published<object>().ShouldBeEmpty();
    }
}
