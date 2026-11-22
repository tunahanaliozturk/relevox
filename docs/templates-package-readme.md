# Relevox.Templates

[GitHub](https://github.com/tunahanaliozturk/relevox) ·
[Changelog](https://github.com/tunahanaliozturk/relevox/blob/main/CHANGELOG.md)

`dotnet new` templates for [Relevox](https://github.com/tunahanaliozturk/relevox), the source-generated
mediator with MediatR 12's handler signatures. The projects they write reference the Relevox version this
package was released with.

```bash
dotnet new install Relevox.Templates
```

## relevox-webapi

An ASP.NET Core minimal API with Relevox registered, one request sent from an endpoint, and an xUnit test
project that calls the endpoint over HTTP.

```bash
dotnet new relevox-webapi -n Shop                     # net10.0
dotnet new relevox-webapi -n Shop --framework net11.0
cd Shop
dotnet test
```

The endpoint is mapped by hand in `Program.cs`.

## relevox-feature

A request and its handler in the application project, and a test for the handler in the test project. Run it in
the folder that holds both, named `<app>` and `<app>.Tests`, which is the layout `relevox-webapi` creates:

```bash
dotnet new relevox-feature -n PlaceOrder --app Shop --response int
```

This writes `Shop/PlaceOrder.cs` and `Shop.Tests/PlaceOrderTests.cs`. `--response` defaults to `string`. The
handler answers `default` until you give it something to do, and the test checks exactly that, so change both.

`--response none` writes a request that implements `IRequest`, with a handler that implements
`IRequestHandler<TRequest>` and returns `Task`, and a test that calls it.

A solution laid out as `src/Shop` and `tests/Shop.Tests` is targeted with two folder options. The namespace is
still the `--app` name:

```bash
dotnet new relevox-feature -n PlaceOrder --app Shop --app-dir src/Shop --tests-dir tests/Shop.Tests
```

`--app-dir` defaults to `<app>` and `--tests-dir` to `<app>.Tests`.
