using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace RelevoxWebApi.Tests;

// The application in process, called over HTTP: the endpoint, the mediator and the handler together.
public sealed class GreetingEndpointTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task The_endpoint_sends_the_request_and_returns_the_answer()
    {
        HttpClient client = _factory.CreateClient();

        Greeting? greeting = await client.GetFromJsonAsync<Greeting>("/greetings/ada", TestContext.Current.CancellationToken);

        Assert.Equal(new Greeting("Hello, ada."), greeting);
    }
}
