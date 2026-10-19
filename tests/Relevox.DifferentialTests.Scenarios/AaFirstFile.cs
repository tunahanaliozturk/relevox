namespace Scenarios.Zeta;

// The first scenario file in compile order, declaring a handler in a nested namespace. Which position it
// takes among the Alarm handlers shows whether "declaration order" means file order or namespace order.
public sealed class ZetaAlarmWatcher(Trace trace) : INotificationHandler<Alarm>
{
    public Task Handle(Alarm notification, CancellationToken cancellationToken)
    {
        trace.Add($"ZetaAlarmWatcher({notification.Zone})");
        return Task.CompletedTask;
    }
}
