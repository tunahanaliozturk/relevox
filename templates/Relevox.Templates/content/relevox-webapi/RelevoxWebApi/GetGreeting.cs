using Relevox;

namespace RelevoxWebApi;

public sealed record GetGreeting(string Name) : IRequest<Greeting>;

public sealed record Greeting(string Text);

public sealed class GetGreetingHandler : IRequestHandler<GetGreeting, Greeting>
{
    public Task<Greeting> Handle(GetGreeting request, CancellationToken cancellationToken) =>
        Task.FromResult(new Greeting($"Hello, {request.Name}."));
}
