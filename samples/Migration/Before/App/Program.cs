using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Shop.Orders;

ServiceCollection services = new();
services.AddSingleton<OrderStore>();
services.AddSingleton<Journal>();
services.AddMediatR(options =>
{
    options.RegisterServicesFromAssemblyContaining<PlaceOrder>();
    options.AddOpenBehavior(typeof(Validation<,>));
    options.AddOpenRequestPreProcessor(typeof(Stamp<>));
});

await using ServiceProvider provider = services.BuildServiceProvider();
using IServiceScope scope = provider.CreateScope();
IMediator mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
Journal journal = provider.GetRequiredService<Journal>();

int first = await mediator.Send(new PlaceOrder("ada", 2));
int second = await mediator.Send(new PlaceOrder("ada", 5));
Console.WriteLine($"placed orders {first} and {second}");

Console.WriteLine($"order {first}: {await mediator.Send(new GetOrder(first))}");
Console.WriteLine($"order 99: {await mediator.Send(new GetOrder(99))}");

try
{
    await mediator.Send(new PlaceOrder("bob", 0));
}
catch (OrderRejectedException rejected)
{
    Console.WriteLine($"rejected: {rejected.Message}");
}

await mediator.Send(new CancelOrder(first));

await foreach (OrderView order in mediator.CreateStream(new ListOrders("ada")))
{
    Console.WriteLine($"listed: {order}");
}

foreach (string line in journal.Lines)
{
    Console.WriteLine($"journal: {line}");
}
