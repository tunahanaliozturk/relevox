using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace Relevox.Internal;

/// <summary>
/// The span around one send, publish or stream enumeration, when telemetry is on. Used by generated code.
/// </summary>
/// <remarks>
/// <para>
/// The generated mediator starts a scope, makes the same call it makes with telemetry off, and hands the
/// outcome to the scope, which records it and passes it on untouched. A synchronous throw is recorded and
/// rethrown by the generated code, so it stays a throw; a task that has already finished is returned as it is;
/// a task still running is followed by a continuation, and the caller gets a task that finishes the same way,
/// every exception and the cancellation included. Names come from the generated code as string constants, so
/// nothing is worked out with reflection when a span starts (ADR 0013).
/// </para>
/// <para>
/// The span becomes <see cref="Activity.Current"/> while the handler runs, so a send from inside a handler is
/// its child. The send itself is not an async method, though, so the caller's context would keep the span as
/// current after the call returns; the scope puts back whatever was current before.
/// </para>
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
[StackTraceHidden, DebuggerNonUserCode, DebuggerStepThrough]
public readonly struct TelemetryScope
{
    private const string Operation = "relevox.operation";
    private const string MessageType = "relevox.message.type";
    private const string HandlerType = "relevox.handler.type";
    private const string HandlerCount = "relevox.notification.handler_count";
    private const string ErrorType = "error.type";

    private static readonly ActivitySource Source = new(
        RelevoxTelemetry.SourceName,
        typeof(TelemetryScope).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    // Notification types the build never saw, named once each. Bounded by the types the application publishes.
    private static readonly ConcurrentDictionary<Type, (string Span, string Message)> RuntimeNames = new();

    private readonly Activity? _activity;
    private readonly Activity? _previous;

    private TelemetryScope(Activity activity, Activity? previous)
    {
        _activity = activity;
        _previous = previous;
    }

    /// <summary>Starts the span for a send.</summary>
    /// <param name="spanName">The span name, such as <c>send PlaceOrder</c>.</param>
    /// <param name="messageType">The request's full type name.</param>
    /// <param name="handlerType">The handler's full type name.</param>
    /// <returns>The scope; an empty one when nothing listens.</returns>
    public static TelemetryScope StartSend(string spanName, string messageType, string handlerType) =>
        Start(spanName, "send", messageType, HandlerType, handlerType);

    /// <summary>Starts the span for one enumeration of a stream.</summary>
    /// <param name="spanName">The span name, such as <c>stream ListOrders</c>.</param>
    /// <param name="messageType">The stream request's full type name.</param>
    /// <param name="handlerType">The handler's full type name.</param>
    /// <returns>The scope; an empty one when nothing listens.</returns>
    public static TelemetryScope StartStream(string spanName, string messageType, string handlerType) =>
        Start(spanName, "stream", messageType, HandlerType, handlerType);

    /// <summary>Starts the span for a publish.</summary>
    /// <param name="spanName">The span name, such as <c>publish OrderPlaced</c>.</param>
    /// <param name="messageType">The notification's full type name.</param>
    /// <param name="handlerCount">How many handlers the notification reaches.</param>
    /// <returns>The scope; an empty one when nothing listens.</returns>
    public static TelemetryScope StartPublish(string spanName, string messageType, int handlerCount) =>
        Start(spanName, "publish", messageType, HandlerCount, handlerCount);

    /// <summary>
    /// Starts the span for a publish of a notification type the build never saw, named from the type itself.
    /// </summary>
    /// <param name="notificationType">The notification's runtime type.</param>
    /// <param name="handlers">The generic handlers that may be closed over it, or null when there are none.</param>
    /// <returns>The scope; an empty one when nothing listens.</returns>
    public static TelemetryScope StartPublish(Type notificationType, OpenNotificationHandlers? handlers)
    {
        ArgumentNullException.ThrowIfNull(notificationType);
        if (!Source.HasListeners())
        {
            return default;
        }

        (string span, string message) = RuntimeNames.GetOrAdd(
            notificationType,
            static type => ("publish " + Name(type, full: false), Name(type, full: true)));
        return StartPublish(span, message, handlers?.HandlerCount(notificationType) ?? 0);
    }

    /// <summary>Records an exception thrown before the operation returned, and ends the span.</summary>
    /// <param name="exception">The exception, which the caller rethrows.</param>
    public void Fail(Exception exception)
    {
        if (_activity is null)
        {
            return;
        }

        Record(_activity, exception);
        _activity.Stop();
        Activity.Current = _previous;
    }

    /// <summary>Ends the span when the task finishes.</summary>
    /// <param name="task">The task the operation returned.</param>
    /// <returns>The same task when it has finished, otherwise one that finishes the same way.</returns>
    public Task Complete(Task task)
    {
        if (_activity is null)
        {
            return task;
        }

        if (task is null || task.IsCompleted)
        {
            End(_activity, task);
            Activity.Current = _previous;
            return task!;
        }

        // Unwrap rather than an async method: it copies every exception and the cancellation token across, so a
        // task from Task.WhenAll still carries all of its exceptions.
        Activity.Current = _previous;
        return task.ContinueWith(
                static (finished, state) =>
                {
                    End((Activity)state!, finished);
                    return finished;
                },
                _activity,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default)
            .Unwrap();
    }

    /// <summary>Ends the span when the task finishes.</summary>
    /// <typeparam name="TResponse">The result type.</typeparam>
    /// <param name="task">The task the operation returned.</param>
    /// <returns>The same task when it has finished, otherwise one that finishes the same way.</returns>
    public Task<TResponse> Complete<TResponse>(Task<TResponse> task)
    {
        if (_activity is null)
        {
            return task;
        }

        if (task is null || task.IsCompleted)
        {
            End(_activity, task);
            Activity.Current = _previous;
            return task!;
        }

        Activity.Current = _previous;
        return task.ContinueWith(
                static (finished, state) =>
                {
                    End((Activity)state!, finished);
                    return finished;
                },
                _activity,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default)
            .Unwrap();
    }

    /// <summary>Ends the span when the stream ends, fails or is disposed.</summary>
    /// <typeparam name="TResponse">The item type.</typeparam>
    /// <param name="stream">The stream the handler, or the outermost behaviour, returned.</param>
    /// <returns>The stream, followed.</returns>
    public IAsyncEnumerable<TResponse> Enumerate<TResponse>(IAsyncEnumerable<TResponse> stream)
    {
        if (_activity is null)
        {
            return stream;
        }

        Activity.Current = _previous;
        return new TracedStream<TResponse>(stream, _activity);
    }

    private static TelemetryScope Start(string spanName, string operation, string messageType, string extraTag, object extraValue)
    {
        if (!Source.HasListeners())
        {
            return default;
        }

        Activity? previous = Activity.Current;
        Activity? activity = Source.StartActivity(spanName, ActivityKind.Internal);
        if (activity is null)
        {
            return default;
        }

        if (activity.IsAllDataRequested)
        {
            activity.SetTag(Operation, operation);
            activity.SetTag(MessageType, messageType);
            activity.SetTag(extraTag, extraValue);
        }

        return new TelemetryScope(activity, previous);
    }

    private static void End(Activity activity, Task? finished)
    {
        if (finished is { IsFaulted: true })
        {
            // The exception awaiting the task throws.
            Record(activity, finished.Exception!.InnerException ?? finished.Exception);
        }
        else if (finished is { IsCanceled: true })
        {
            Record(activity, new TaskCanceledException(finished));
        }

        activity.Stop();
    }

    private static void Record(Activity activity, Exception exception)
    {
        activity.SetStatus(ActivityStatusCode.Error);
        if (activity.IsAllDataRequested)
        {
            activity.SetTag(ErrorType, exception.GetType().FullName);
            activity.AddException(exception);
        }
    }

    /// <summary>
    /// A type's name as the generator writes it: <c>Type.FullName</c> or <c>Type.Name</c> with the generic arity
    /// marks dropped, nested types joined with a dot, and generic arguments in angle brackets.
    /// </summary>
    /// <remarks>
    /// Only <c>Name</c>, <c>FullName</c> and the generic arguments are read, which trimming and NativeAOT keep.
    /// </remarks>
    private static string Name(Type type, bool full)
    {
        if (type.IsArray)
        {
            return Name(type.GetElementType()!, full) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
        }

        Type definition = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
        string raw = full ? definition.FullName ?? definition.Name : definition.Name;
        StringBuilder name = new(raw.Length);
        for (int index = 0; index < raw.Length; index++)
        {
            if (raw[index] == '`')
            {
                // The arity mark, as in Paged`1.
                while (index + 1 < raw.Length && char.IsAsciiDigit(raw[index + 1]))
                {
                    index++;
                }

                continue;
            }

            name.Append(raw[index] == '+' ? '.' : raw[index]);
        }

        if (!type.IsGenericType)
        {
            return name.ToString();
        }

        name.Append('<');
        Type[] arguments = type.GetGenericArguments();
        for (int index = 0; index < arguments.Length; index++)
        {
            name.Append(index == 0 ? "" : ", ").Append(Name(arguments[index], full));
        }

        return name.Append('>').ToString();
    }

    /// <summary>Follows one enumeration of a stream and ends its span at the end, on a failure, or on disposal.</summary>
    /// <remarks>
    /// The handler's iterator runs inside each <c>MoveNextAsync</c>, so the span is made current for each call
    /// and the caller's own current activity is put back afterwards. Exceptions keep their timing: a throw from
    /// <c>MoveNextAsync</c> is still a throw, and a faulted result still a faulted result.
    /// </remarks>
    [StackTraceHidden, DebuggerNonUserCode, DebuggerStepThrough]
    private sealed class TracedStream<TResponse>(IAsyncEnumerable<TResponse> source, Activity activity)
        : IAsyncEnumerable<TResponse>, IAsyncEnumerator<TResponse>
    {
        private IAsyncEnumerator<TResponse>? _inner;
        private bool _ended;

        public TResponse Current => _inner is null ? default! : _inner.Current;

        // Enumerated once, by the DeferredEnumerable that opened it.
        public IAsyncEnumerator<TResponse> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            Activity? previous = Activity.Current;
            Activity.Current = activity;
            try
            {
                _inner = source.GetAsyncEnumerator(cancellationToken);
                return this;
            }
            catch (Exception exception)
            {
                End(exception);
                throw;
            }
            finally
            {
                Activity.Current = previous;
            }
        }

        public ValueTask<bool> MoveNextAsync()
        {
            if (_ended)
            {
                return _inner!.MoveNextAsync();
            }

            Activity? previous = Activity.Current;
            Activity.Current = activity;
            try
            {
                ValueTask<bool> move = _inner!.MoveNextAsync();
                if (!move.IsCompletedSuccessfully)
                {
                    return Await(move);
                }

                // Read once and handed on as a value: a ValueTask backed by a source may not be read twice.
                bool more = move.Result;
                if (!more)
                {
                    End(null);
                }

                return new ValueTask<bool>(more);
            }
            catch (Exception exception)
            {
                End(exception);
                throw;
            }
            finally
            {
                Activity.Current = previous;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_inner is not null)
                {
                    await _inner.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                End(exception);
                throw;
            }

            End(null);
        }

        private async ValueTask<bool> Await(ValueTask<bool> move)
        {
            try
            {
                bool more = await move.ConfigureAwait(false);
                if (!more)
                {
                    End(null);
                }

                return more;
            }
            catch (Exception exception)
            {
                End(exception);
                throw;
            }
        }

        private void End(Exception? exception)
        {
            if (_ended)
            {
                return;
            }

            _ended = true;
            if (exception is not null)
            {
                Record(activity, exception);
            }

            activity.Stop();
        }
    }
}
