using Microsoft.Extensions.DependencyInjection;

namespace Scenarios;

/// <summary>
/// Every public static method here is one scenario, run once against each library and compared.
/// </summary>
/// <remarks>
/// A scenario describes everything a caller can observe: the result, which handlers ran in what order, and
/// the type of any exception, including whether it escaped synchronously or inside the task.
/// </remarks>
public static class Scenarios
{
    // Requests

    public static Task<string> SendThroughSender() =>
        Run.Scenario(async services => await services.GetRequiredService<ISender>().Send(new Ping("ping")));

    public static Task<string> SendThroughMediator() =>
        Run.Scenario(async services => await services.GetRequiredService<IMediator>().Send(new Ping("hello")));

    public static Task<string> SendVoidRequest() =>
        Run.Scenario(async services =>
        {
            await services.GetRequiredService<ISender>().Send(new Ring(2));
            return "done";
        });

    public static Task<string> SendObjectWithResponse() =>
        Run.Scenario(async services => await services.GetRequiredService<ISender>().Send((object)new Ping("boxed")));

    public static Task<string> SendObjectVoidRequest() =>
        Run.Scenario(async services => await services.GetRequiredService<ISender>().Send((object)new Ring(1)));

    public static Task<string> SendObjectThatIsNotARequest() =>
        Run.Scenario(services => Run.Timing(() => services.GetRequiredService<ISender>().Send((object)"not a request")));

    public static Task<string> SendObjectWithoutHandler() =>
        Run.Scenario(services => Run.Timing(() => services.GetRequiredService<ISender>().Send((object)new Orphan())));

    public static Task<string> SendNull() =>
        Run.Scenario(services => Run.Timing(() => services.GetRequiredService<ISender>().Send((IRequest<string>)null!)));

    public static Task<string> SendObjectNull() =>
        Run.Scenario(services => Run.Timing(() => services.GetRequiredService<ISender>().Send((object)null!)));

    public static Task<string> SendWithoutHandler() =>
        Run.Scenario(services => Run.Timing(() => services.GetRequiredService<ISender>().Send(new Orphan())));

    public static Task<string> SendDerivedRequestWhoseBaseHasHandler() =>
        Run.Scenario(services => Run.Timing(() => services.GetRequiredService<ISender>().Send(new DerivedQuery())));

    public static Task<string> SendToHandlerThatThrows() =>
        Run.Scenario(services => Run.Timing(() => services.GetRequiredService<ISender>().Send(new Explode())));

    public static Task<string> TransientHandlerIsNewEachCall() =>
        Run.Scenario(async services =>
        {
            ISender sender = services.GetRequiredService<ISender>();
            int first = await sender.Send(new Stamp());
            int second = await sender.Send(new Stamp());
            return first == second ? "same instance" : "new instance per call";
        });

    public static Task<string> ScopedHandlerIsSharedWithinAScope() =>
        Run.Scenario(
            async services =>
            {
                ISender sender = services.GetRequiredService<ISender>();
                return await sender.Send(new Stamp()) == await sender.Send(new Stamp()) ? "same instance" : "new instance per call";
            },
            lifetime: ServiceLifetime.Scoped);

    public static Task<string> SingletonHandlerIsShared() =>
        Run.Scenario(
            async services =>
            {
                ISender sender = services.GetRequiredService<ISender>();
                return await sender.Send(new Stamp()) == await sender.Send(new Stamp()) ? "same instance" : "new instance per call";
            },
            lifetime: ServiceLifetime.Singleton);

    public static Task<string> CancellationTokenReachesHandler() =>
        Run.Scenario(async services =>
        {
            using CancellationTokenSource source = new();
            return await services.GetRequiredService<ISender>().Send(new TokenCheck(), source.Token);
        });

    // Notifications

    public static Task<string> PublishToSeveralHandlers() =>
        Run.Scenario(
            async services =>
            {
                await services.GetRequiredService<IPublisher>().Publish(new Alarm("north"));
                return "done";
            },
            TraceOrder.Unordered);

    public static Task<string> PublishObject() =>
        Run.Scenario(
            async services =>
            {
                await services.GetRequiredService<IPublisher>().Publish((object)new Alarm("south"));
                return "done";
            },
            TraceOrder.Unordered);

    public static Task<string> PublishWithOnlyCatchAllHandler() =>
        Run.Scenario(
            async services =>
            {
                await services.GetRequiredService<IMediator>().Publish(new Quiet());
                return "done";
            },
            TraceOrder.Unordered);

    public static Task<string> PublishThroughBaseTypeUsesRuntimeType() =>
        Run.Scenario(
            async services =>
            {
                await services.GetRequiredService<IPublisher>().Publish<Signal>(new Alarm("east"));
                return "done";
            },
            TraceOrder.Unordered);

    public static Task<string> PublishClosedGenericNotification() =>
        Run.Scenario(
            async services =>
            {
                await services.GetRequiredService<IPublisher>().Publish(new EntityCreated<Order>(new Order(7)));
                return "done";
            },
            TraceOrder.Unordered);

    public static Task<string> PublishStructNotification() =>
        Run.Scenario(
            async services =>
            {
                await services.GetRequiredService<IPublisher>().Publish(new Beep(3));
                return "done";
            },
            TraceOrder.Unordered);

    public static Task<string> PublishWhenAHandlerCannotBeBuilt() =>
        Run.Scenario(services => Run.Timing(() => services.GetRequiredService<IPublisher>().Publish(new Fragile())));

    public static Task<string> PublishObjectThatIsNotANotification() =>
        Run.Scenario(services => Run.Timing(() => services.GetRequiredService<IPublisher>().Publish((object)"not a notification")));

    public static Task<string> PublishNull() =>
        Run.Scenario(services => Run.Timing(() => services.GetRequiredService<IPublisher>().Publish((Alarm)null!)));

    public static Task<string> PublishToHandlerThatThrows() =>
        Run.Scenario(
            services => Run.Timing(() => services.GetRequiredService<IPublisher>().Publish(new Boom())),
            TraceOrder.Ignored);

    // Streams

    public static Task<string> StreamItems() =>
        Run.Scenario(services => Run.StreamTiming(() => services.GetRequiredService<ISender>().CreateStream(new Countdown(3))));

    public static Task<string> StreamObject() =>
        Run.Scenario(services => Run.StreamTiming(() => services.GetRequiredService<ISender>().CreateStream((object)new Countdown(2))));

    public static Task<string> StreamWithoutHandler() =>
        Run.Scenario(services => Run.StreamTiming(() => services.GetRequiredService<ISender>().CreateStream(new Silence())));

    public static Task<string> StreamObjectThatIsNotAStream() =>
        Run.Scenario(services => Run.StreamTiming(() => services.GetRequiredService<ISender>().CreateStream((object)"not a stream")));

    public static Task<string> StreamTokenFromWithCancellationReachesHandler() =>
        Run.Scenario(async services =>
        {
            using CancellationTokenSource source = new();
            List<int> items = [];
            await foreach (int item in services.GetRequiredService<ISender>().CreateStream(new Ticker(2)).WithCancellation(source.Token))
            {
                items.Add(item);
            }

            return items.Count;
        });

    public static Task<string> StreamEnumeratedTwiceResolvesTheHandlerTwice() =>
        Run.Scenario(async services =>
        {
            IAsyncEnumerable<int> stream = services.GetRequiredService<ISender>().CreateStream(new Ticker(1));
            List<int> instances = [];
            await foreach (int instance in stream)
            {
                instances.Add(instance);
            }

            await foreach (int instance in stream)
            {
                instances.Add(instance);
            }

            return instances[0] == instances[1] ? "same handler" : "new handler per enumeration";
        });

    public static Task<string> StreamNull() =>
        Run.Scenario(services => Run.StreamTiming(() => services.GetRequiredService<ISender>().CreateStream((IStreamRequest<int>)null!)));
}
