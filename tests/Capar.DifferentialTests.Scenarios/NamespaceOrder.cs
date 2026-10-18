using Microsoft.Extensions.DependencyInjection;

// Handlers spread over sibling and nested namespaces, declared out of alphabetical order. MediatR runs them in
// the order the compiler wrote their types into metadata, which is not the order they appear here.

namespace Scenarios.Zeta
{
    public sealed class RollZeta(Trace trace) : INotificationHandler<global::Scenarios.Roll>
    {
        public Task Handle(global::Scenarios.Roll notification, CancellationToken cancellationToken)
        {
            trace.Add("Zeta");
            return Task.CompletedTask;
        }
    }

    public sealed class RollAudit<T>(Trace trace) : INotificationHandler<T>
        where T : global::Scenarios.IRollCall
    {
        public Task Handle(T notification, CancellationToken cancellationToken)
        {
            trace.Add("Zeta generic");
            return Task.CompletedTask;
        }
    }
}

namespace Scenarios.Alpha
{
    public sealed class RollAlpha(Trace trace) : INotificationHandler<global::Scenarios.Roll>
    {
        public Task Handle(global::Scenarios.Roll notification, CancellationToken cancellationToken)
        {
            trace.Add("Alpha");
            return Task.CompletedTask;
        }
    }

    public sealed class RollAudit<T>(Trace trace) : INotificationHandler<T>
        where T : global::Scenarios.IRollCall
    {
        public Task Handle(T notification, CancellationToken cancellationToken)
        {
            trace.Add("Alpha generic");
            return Task.CompletedTask;
        }
    }

    namespace Inner
    {
        public sealed class RollInner(Trace trace) : INotificationHandler<global::Scenarios.Roll>
        {
            public Task Handle(global::Scenarios.Roll notification, CancellationToken cancellationToken)
            {
                trace.Add("Alpha.Inner");
                return Task.CompletedTask;
            }
        }
    }

    namespace Early
    {
        public sealed class RollEarly(Trace trace) : INotificationHandler<global::Scenarios.Roll>
        {
            public Task Handle(global::Scenarios.Roll notification, CancellationToken cancellationToken)
            {
                trace.Add("Alpha.Early");
                return Task.CompletedTask;
            }
        }
    }
}

namespace Scenarios.Mid
{
    public sealed class RollMid(Trace trace) : INotificationHandler<global::Scenarios.Roll>
    {
        public Task Handle(global::Scenarios.Roll notification, CancellationToken cancellationToken)
        {
            trace.Add("Mid");
            return Task.CompletedTask;
        }
    }
}

namespace Scenarios
{
    public interface IRollCall : INotification;

    public sealed record Roll : IRollCall;

    public static partial class Scenarios
    {
        public static Task<string> HandlersInSiblingNamespacesRunInMetadataOrder() =>
            Run.Scenario(async services =>
            {
                await services.GetRequiredService<IPublisher>().Publish(new Roll());
                return "done";
            });
    }
}
