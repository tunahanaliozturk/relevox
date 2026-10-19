using Microsoft.Extensions.DependencyInjection;

// Handlers spread over sibling and nested namespaces, declared out of alphabetical order. MediatR runs them in
// the order the compiler wrote their types into metadata, which is not the order they appear here. Nested
// types come after every top-level type, one level at a time.

namespace Scenarios.Zeta
{
    public static class Holder
    {
        public sealed class RollNested(Trace trace) : INotificationHandler<global::Scenarios.Roll>
        {
            public Task Handle(global::Scenarios.Roll notification, CancellationToken cancellationToken)
            {
                trace.Add("Zeta.Holder.Nested");
                return Task.CompletedTask;
            }
        }

        public static class Deeper
        {
            public sealed class RollDeep(Trace trace) : INotificationHandler<global::Scenarios.Roll>
            {
                public Task Handle(global::Scenarios.Roll notification, CancellationToken cancellationToken)
                {
                    trace.Add("Zeta.Holder.Deeper.Deep");
                    return Task.CompletedTask;
                }
            }
        }

        public sealed class RollAuditNested<T>(Trace trace) : INotificationHandler<T>
            where T : global::Scenarios.IRollCall
        {
            public Task Handle(T notification, CancellationToken cancellationToken)
            {
                trace.Add("Zeta.Holder generic");
                return Task.CompletedTask;
            }
        }
    }

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

    public sealed record RollOf<T>(T Value) : IRollCall;

    public static partial class Scenarios
    {
        public static Task<string> HandlersInSiblingNamespacesRunInMetadataOrder() =>
            Run.Scenario(async services =>
            {
                await services.GetRequiredService<IPublisher>().Publish(new Roll());
                return "done";
            });

        // RollOf<int> is named nowhere, so only the generic handlers closed at run time reach it, in the same order.
        public static Task<string> GenericHandlersInNamespacesAndNestedTypesReachATypeTheBuildNeverSaw() =>
            Run.Scenario(async services =>
            {
                await services.GetRequiredService<IPublisher>().Publish(Rolls.Of(5));
                return "done";
            });
    }

    public static class Rolls
    {
        public static INotification Of<T>(T value) => new RollOf<T>(value);
    }
}
