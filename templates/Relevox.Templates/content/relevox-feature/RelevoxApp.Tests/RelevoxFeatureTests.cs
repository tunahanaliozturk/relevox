using Xunit;

namespace RelevoxApp.Tests;

public sealed class RelevoxFeatureTests
{
    [Fact]
    public async Task The_handler_answers()
    {
        var handler = new RelevoxFeatureHandler();

        RelevoxResponse response = await handler.Handle(new RelevoxFeature(), TestContext.Current.CancellationToken);

        Assert.Equal(default(RelevoxResponse), response);
    }
}
