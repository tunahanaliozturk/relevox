using Xunit;

namespace RelevoxApp.Tests;

public sealed class RelevoxFeatureTests
{
#if (noResponse)
    [Fact]
    public async Task The_handler_completes()
    {
        var handler = new RelevoxFeatureHandler();

        Task task = handler.Handle(new RelevoxFeature(), TestContext.Current.CancellationToken);
        await task;

        Assert.True(task.IsCompletedSuccessfully);
    }
#else
    [Fact]
    public async Task The_handler_answers()
    {
        var handler = new RelevoxFeatureHandler();

        RelevoxResponse response = await handler.Handle(new RelevoxFeature(), TestContext.Current.CancellationToken);

        Assert.Equal(default(RelevoxResponse), response);
    }
#endif
}
