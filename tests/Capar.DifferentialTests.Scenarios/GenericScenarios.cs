using Microsoft.Extensions.DependencyInjection;

namespace Scenarios;

public static partial class Scenarios
{
    public static Task<string> GenericNotificationHandlersRunAfterTheWrittenOne() =>
        Run.Scenario(async services =>
        {
            await services.GetRequiredService<IPublisher>().Publish(new Dispatched("p1"));
            return "done";
        });

    public static Task<string> GenericNotificationHandlersReachANotificationNothingElseHandles() =>
        Run.Scenario(async services =>
        {
            await services.GetRequiredService<IPublisher>().Publish(new Delivered("p2"));
            return "done";
        });

    public static Task<string> GenericNotificationHandlersAsObject() =>
        Run.Scenario(async services =>
        {
            await services.GetRequiredService<IPublisher>().Publish((object)new Delivered("p3"));
            return "done";
        });

    public static Task<string> GenericNotificationHandlerConstraintsAreRespectedForStructs() =>
        Run.Scenario(async services =>
        {
            await services.GetRequiredService<IPublisher>().Publish(new Scanned("p4"));
            return "done";
        });

    public static Task<string> GenericNotificationHandlerReachesAClosedGenericNotification() =>
        Run.Scenario(async services =>
        {
            await services.GetRequiredService<IPublisher>().Publish(new Rerouted<string>("Izmir"));
            return "done";
        });

    public static Task<string> GenericNotificationHandlerSkipsANotificationOutsideItsConstraint() =>
        Run.Scenario(async services =>
        {
            await services.GetRequiredService<IPublisher>().Publish(new Mislaid("p5"));
            return "done";
        });

    public static Task<string> GenericNotificationHandlersUnderWhenAll() =>
        Run.Scenario(
            async services =>
            {
                await services.GetRequiredService<IPublisher>().Publish(new Dispatched("p6"));
                return "done";
            },
            profile: Profile.WhenAll);

    public static Task<string> GenericRequestHandlerClosedOverAConstrainedType() =>
        Run.Scenario(async services => await services.GetRequiredService<ISender>().Send(new StockLevel<Bolt>(3)), profile: Profile.GenericHandlers);

    public static Task<string> GenericRequestHandlerAsObject() =>
        Run.Scenario(async services => await services.GetRequiredService<ISender>().Send((object)new StockLevel<Washer>(4)), profile: Profile.GenericHandlers);

    public static Task<string> GenericVoidRequestHandler() =>
        Run.Scenario(
            async services =>
            {
                await services.GetRequiredService<ISender>().Send(new Restock<Nut>(12));
                return "done";
            },
            profile: Profile.GenericHandlers);

    // MediatR's default registers only the written handler. With RegisterGenericHandlers it registers the
    // closing too, after it, and the container returns the closing; Capar keeps the written one in both cases.
    public static Task<string> GenericRequestHandlerYieldsToAWrittenOne() =>
        Run.Scenario(async services => await services.GetRequiredService<ISender>().Send(new StockLevel<Nut>(5)));
}
