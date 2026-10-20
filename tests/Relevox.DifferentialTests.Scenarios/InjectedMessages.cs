namespace Scenarios;

// Handlers that other code takes through their interface rather than through the mediator, which MediatR's
// registration allows. A real migration found the first one: a query handler took another handler as
// IRequestHandler<,>, and the host failed its startup validation because Relevox registered only the class.
// Every scenario builds its container with ValidateOnBuild, so these break all of them if the interface goes
// unregistered again.

/// <summary>Handled by <see cref="SummaryHandler"/>, which calls the handler for <see cref="Tally"/> itself.</summary>
public sealed record Summary(string Shelf) : IRequest<string>;

public sealed record Tally(string Shelf) : IRequest<int>;

public sealed class TallyHandler(Trace trace) : IRequestHandler<Tally, int>
{
    public Task<int> Handle(Tally request, CancellationToken cancellationToken)
    {
        trace.Add($"TallyHandler({request.Shelf})");
        return Task.FromResult(request.Shelf.Length);
    }
}

public sealed class SummaryHandler(IRequestHandler<Tally, int> tally, Trace trace) : IRequestHandler<Summary, string>
{
    public async Task<string> Handle(Summary request, CancellationToken cancellationToken)
    {
        int count = await tally.Handle(new Tally(request.Shelf), cancellationToken);
        trace.Add("SummaryHandler");
        return $"{request.Shelf}: {count}";
    }
}

/// <summary>Rung by code that takes every handler for it from the container and calls them itself.</summary>
public sealed record Doorbell(string Tone) : INotification;

public sealed class FirstDoorbellListener(Trace trace) : INotificationHandler<Doorbell>
{
    public Task Handle(Doorbell notification, CancellationToken cancellationToken)
    {
        trace.Add($"FirstDoorbellListener({notification.Tone})");
        return Task.CompletedTask;
    }
}

public sealed class SecondDoorbellListener(Trace trace) : INotificationHandler<Doorbell>
{
    public Task Handle(Doorbell notification, CancellationToken cancellationToken)
    {
        trace.Add($"SecondDoorbellListener({notification.Tone})");
        return Task.CompletedTask;
    }
}

/// <summary>An abstract notification with a handler of its own, taken from the container as a list.</summary>
public abstract record Bulletin(string Text) : INotification;

public sealed record PostedBulletin(string Text) : Bulletin(Text);

public sealed class BulletinReader(Trace trace) : INotificationHandler<Bulletin>
{
    public Task Handle(Bulletin notification, CancellationToken cancellationToken)
    {
        trace.Add($"BulletinReader({notification.Text})");
        return Task.CompletedTask;
    }
}

/// <summary>The same for a notification interface.</summary>
public interface IMemo : INotification
{
    string Text { get; }
}

public sealed record Memo(string Text) : IMemo;

public sealed class MemoReader(Trace trace) : INotificationHandler<IMemo>
{
    public Task Handle(IMemo notification, CancellationToken cancellationToken)
    {
        trace.Add($"MemoReader({notification.Text})");
        return Task.CompletedTask;
    }
}
