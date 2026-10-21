using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Relevox;
using Shop;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<OrderStore>();
builder.Services.AddSingleton<Activity>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentCustomer>();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ProblemMapper>();

// The whole registration. Handlers are found while the project builds; the two lines add what MediatR's
// configuration would have added: an open behaviour and an open pre-processor.
builder.Services.AddRelevox(options =>
{
    options.AddOpenBehavior(typeof(Validation<,>));
    options.AddOpenRequestPreProcessor(typeof(Received<>));
    options.Telemetry.Enabled = true;
});

// Spans and metrics go out over OTLP only when an endpoint is configured, through the variable every
// OpenTelemetry SDK reads, so a plain `dotnet run` and the tests export nothing. README.md shows it against the
// Aspire dashboard.
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService("Relevox.Samples.Web"))
        .WithTracing(tracing => tracing
            .AddAspNetCoreInstrumentation()
            .AddSource(RelevoxTelemetry.SourceName)
            .AddOtlpExporter())
        .WithMetrics(metrics => metrics
            .AddAspNetCoreInstrumentation()
            .AddMeter(RelevoxTelemetry.SourceName)
            .AddOtlpExporter());
}

WebApplication app = builder.Build();
app.UseExceptionHandler();

// Every endpoint takes ISender from the request's scope, so handlers share that scope's CurrentCustomer.
app.MapPost("/orders", async (PlaceOrderBody body, ISender sender, CancellationToken cancellationToken) =>
{
    int id = await sender.Send(new PlaceOrder(body.Product, body.Quantity), cancellationToken);
    return Results.Created($"/orders/{id}", new { id });
});

app.MapGet("/orders/{id:int}", (int id, ISender sender, CancellationToken cancellationToken) =>
    sender.Send(new GetOrder(id), cancellationToken));

app.MapPost("/orders/{id:int}/cancel", async (int id, ISender sender, CancellationToken cancellationToken) =>
{
    await sender.Send(new CancelOrder(id), cancellationToken);
    return Results.NoContent();
});

// Streamed: ASP.NET Core writes each order as the handler yields it.
app.MapGet("/orders", (ISender sender, CancellationToken cancellationToken) =>
    sender.CreateStream(new ListMyOrders(), cancellationToken));

app.MapGet("/activity", (Activity activity) => activity.Lines);

app.Run();

/// <summary>Visible to the tests' WebApplicationFactory.</summary>
public partial class Program;
