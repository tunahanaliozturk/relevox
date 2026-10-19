using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace Relevox.Internal;

/// <summary>
/// Generic notification handlers, closed at run time over a notification type the build never saw. Used by
/// generated code.
/// </summary>
/// <remarks>
/// <para>
/// A handler declared as <c>Audit&lt;T&gt; : INotificationHandler&lt;T&gt;</c> is closed at build time over every
/// notification type the generator can see. Some it cannot: <c>EntityCreated&lt;Invoice&gt;</c>, built inside a
/// generic base class as <c>new EntityCreated&lt;TSelf&gt;()</c>, is a type no declaration names. MediatR registers
/// such a handler open and the container closes it for whatever is published, so this does the same, for the
/// notification types the generated mediator has no entry for, and only those: the build already closed the
/// handler for the rest.
/// </para>
/// <para>
/// Closing a type at run time is the one place Relevox uses <c>MakeGenericType</c>. Each notification type is
/// closed once and remembered. Under NativeAOT a closing over a reference type uses the shared code the
/// compiler already has for the handler; one over a struct cannot, and is skipped, as Microsoft's container
/// would refuse it there too.
/// </para>
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
[StackTraceHidden, DebuggerNonUserCode, DebuggerStepThrough]
public sealed class OpenNotificationHandlers
{
    private readonly Type[] _definitions;
    private readonly ConcurrentDictionary<Type, Closing> _closings = new();

    /// <summary>Creates the set.</summary>
    /// <param name="definitions">The unbound handler types, in the order they run.</param>
    public OpenNotificationHandlers(params Type[] definitions) => _definitions = definitions;

    /// <summary>Publishes a notification to every generic handler that can be closed over its type.</summary>
    /// <param name="services">The container or scope.</param>
    /// <param name="notification">The notification.</param>
    /// <param name="publisher">The configured publisher, or null to run the handlers one after another.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes when the handlers have run.</returns>
    public Task Publish(IServiceProvider services, INotification notification, INotificationPublisher? publisher, CancellationToken cancellationToken)
    {
        Closing closing = _closings.GetOrAdd(notification.GetType(), Close, _definitions);
        if (closing.Failure is { } failure)
        {
            throw failure;
        }

        if (closing.Handlers.Length == 0)
        {
            // A configured publisher is still called with no handlers, as MediatR calls it.
            return publisher is null ? Task.CompletedTask : publisher.Publish([], notification, cancellationToken);
        }

        // Every handler is created before any runs, as for the notification types the build saw, so one that
        // cannot be constructed fails the publish before the others have done anything.
        object[] handlers = new object[closing.Handlers.Length];
        for (int index = 0; index < handlers.Length; index++)
        {
            handlers[index] = services.GetRequiredService(closing.Handlers[index]);
        }

        if (publisher is null)
        {
            return Sequentially(closing.Invoker!, handlers, notification, cancellationToken);
        }

        NotificationHandlerExecutor[] executors = new NotificationHandlerExecutor[handlers.Length];
        for (int index = 0; index < handlers.Length; index++)
        {
            object handler = handlers[index];
            executors[index] = new(handler, (message, token) => closing.Invoker!.Handle(handler, message, token));
        }

        return publisher.Publish(executors, notification, cancellationToken);
    }

    private static async Task Sequentially(Invoker invoker, object[] handlers, INotification notification, CancellationToken cancellationToken)
    {
        foreach (object handler in handlers)
        {
            await invoker.Handle(handler, notification, cancellationToken).ConfigureAwait(false);
        }
    }

    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Structs are skipped where code cannot be generated; a closing over a reference type uses the handler's shared code.")]
    [UnconditionalSuppressMessage("Trimming", "IL2055", Justification = "The warning is about a type argument meeting [DynamicallyAccessedMembers] on the definition's type parameter; the generator leaves any definition with such a parameter out of this list.")]
    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "The invoker's constructor is kept by the DynamicDependency below.")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor, typeof(Invoker<>))]
    private static Closing Close(Type notification, Type[] definitions)
    {
        if (!RuntimeFeature.IsDynamicCodeSupported && notification.IsValueType)
        {
            return Closing.None;
        }

        List<Type> handlers = [];
        foreach (Type definition in definitions)
        {
            try
            {
                handlers.Add(definition.MakeGenericType(notification));
            }
            catch (ArgumentException)
            {
                // The notification does not satisfy the handler's constraints, so the container would not close
                // it either.
            }
            catch (NotSupportedException exception)
            {
                // NativeAOT has no code for this closing: nothing in the build closed the handler over a reference
                // type, so the compiler kept no shared code to build it from. Skipping it would change which
                // handlers run without a word, so every publish of this type says so instead.
                return new Closing([], null, new NotSupportedException(
                    $"'{definition}' could not be closed over '{notification}' in this NativeAOT build. Handle '{notification}' in a handler the build can see, so the handler is closed while the project builds.",
                    exception));
            }
        }

        return handlers.Count == 0
            ? Closing.None
            : new Closing([.. handlers], (Invoker)Activator.CreateInstance(typeof(Invoker<>).MakeGenericType(notification))!);
    }

    private sealed record Closing(Type[] Handlers, Invoker? Invoker, NotSupportedException? Failure = null)
    {
        public static Closing None { get; } = new([], null);
    }

    /// <summary>Calls a closed handler held as an object, without reflection on each call.</summary>
    private abstract class Invoker
    {
        public abstract Task Handle(object handler, INotification notification, CancellationToken cancellationToken);
    }

    private sealed class Invoker<TNotification> : Invoker
        where TNotification : INotification
    {
        public override Task Handle(object handler, INotification notification, CancellationToken cancellationToken) =>
            ((INotificationHandler<TNotification>)handler).Handle((TNotification)notification, cancellationToken);
    }
}
