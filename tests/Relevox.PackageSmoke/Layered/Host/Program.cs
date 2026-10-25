using App;
using Relevox;
using Microsoft.Extensions.DependencyInjection;

// The generator runs here too, because analyzers flow through project references. With no handlers of its own
// and no AddRelevox call, this project has to generate nothing and report nothing, warnings as errors and all.
await using ServiceProvider provider = new ServiceCollection().AddApplication().BuildServiceProvider();
#if SMOKE_NO_CALLS
// Built this way the host sends nothing, so its message map has nothing to list (ADR 0021).
Console.WriteLine(provider.GetRequiredService<ISender>().GetType().Name);
#else
Console.WriteLine($"answer {await provider.GetRequiredService<ISender>().Send(new Contracts.Ping())}");
#endif
