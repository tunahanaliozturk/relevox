using App;
using Relevox;
using Microsoft.Extensions.DependencyInjection;

// The generator runs here too, because analyzers flow through project references. With no handlers of its own
// and no AddRelevox call, this project has to generate nothing and report nothing, warnings as errors and all.
await using ServiceProvider provider = new ServiceCollection().AddApplication().BuildServiceProvider();
Console.WriteLine($"answer {await provider.GetRequiredService<ISender>().Send(new Contracts.Ping())}");
