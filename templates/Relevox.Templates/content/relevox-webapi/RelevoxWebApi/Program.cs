using Relevox;
using RelevoxWebApi;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// The whole registration: the handlers were found while the project built.
builder.Services.AddRelevox();

WebApplication app = builder.Build();

// Each endpoint is mapped by hand and sends one request. ISender comes from the request's scope.
app.MapGet("/greetings/{name}", (string name, ISender sender, CancellationToken cancellationToken) =>
    sender.Send(new GetGreeting(name), cancellationToken));

app.Run();
