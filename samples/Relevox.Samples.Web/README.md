# Relevox web sample

An ASP.NET Core minimal API on Relevox: endpoints that send, a scoped service the handlers share, a validation
behaviour, a pre-processor, a notification with two listeners, and a streamed list.
[`Relevox.Samples.Web.http`](Relevox.Samples.Web.http) walks through every endpoint, and
[`tests/Relevox.Samples.Web.Tests`](../../tests/Relevox.Samples.Web.Tests) drives the same requests in CI.

```sh
dotnet run --project samples/Relevox.Samples.Web
```

It listens on `http://localhost:5199`.

## Seeing the telemetry

The sample turns Relevox telemetry on, and when `OTEL_EXPORTER_OTLP_ENDPOINT` is set it exports the spans and
metrics over OTLP, along with ASP.NET Core's own. Without the variable it exports nothing, which is how
`dotnet run` and the tests run it.

Any OTLP backend works. The quickest to try is the standalone Aspire dashboard, one container with no Aspire
project around it:

```sh
docker run --rm -it -p 18888:18888 -p 4317:18889 --name aspire-dashboard mcr.microsoft.com/dotnet/aspire-dashboard:13.5.2
```

Port 18888 is the dashboard itself and 4317 is where it takes OTLP over gRPC, with no key. The container prints a
login link with a token when it starts; open that link rather than the bare address.

In a second terminal, run the sample pointed at it:

```sh
# bash
OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317 OTEL_METRIC_EXPORT_INTERVAL=5000 dotnet run --project samples/Relevox.Samples.Web
```

```powershell
# PowerShell
$env:OTEL_EXPORTER_OTLP_ENDPOINT = "http://localhost:4317"
$env:OTEL_METRIC_EXPORT_INTERVAL = "5000"
dotnet run --project samples/Relevox.Samples.Web
```

`OTEL_METRIC_EXPORT_INTERVAL` is optional. Metrics are exported once a minute by default, and five seconds makes
the dashboard catch up while you watch.

Then send a few requests from the `.http` file, or with curl:

```sh
curl -X POST http://localhost:5199/orders -H "Content-Type: application/json" -H "X-Customer: ada" -d '{"product":"kettle","quantity":2}'
curl http://localhost:5199/orders/1
curl http://localhost:5199/orders/999
curl -X POST http://localhost:5199/orders/1/cancel
curl http://localhost:5199/orders -H "X-Customer: ada"
```

In the dashboard, **Traces** shows each request with the Relevox spans under it. Placing an order is a
`send PlaceOrder` span under `POST /orders`, cancelling one is `send CancelOrder` with `publish OrderCancelled`
under it, and the order that does not exist is a `send GetOrder` span marked as an error. **Metrics** has
`relevox.operation.duration`, which can be split by `relevox.operation`, `relevox.message.type` and
`error.type`.

[docs/telemetry.md](../../docs/telemetry.md) lists every span, attribute and instrument.
