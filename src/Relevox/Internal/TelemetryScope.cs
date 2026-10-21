using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Text;

namespace Relevox.Internal;

/// <summary>
/// The span and the duration measurement around one send, publish or stream enumeration, when telemetry is on.
/// Used by generated code.
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
/// The span and the measurement are independent: a tracer without a meter gets spans only, and the other way
/// round. With neither listening, starting a scope asks the two whether anyone listens and returns an empty one.
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

    private static readonly string? Version =
        typeof(TelemetryScope).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    private static readonly ActivitySource Source = new(RelevoxTelemetry.SourceName, Version);

    private static readonly Meter Meter = new(RelevoxTelemetry.SourceName, Version);

    // Buckets in seconds, as OpenTelemetry advises for HTTP durations: the SDK's default buckets assume
    // milliseconds and would put nearly every call in the first one.
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(
        "relevox.operation.duration",
        unit: "s",
        description: "How long a send, a publish or one enumeration of a stream took.",
        tags: null,
        advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10] });

    // Notification types the build never saw, named once each. Bounded by the types the application publishes.
    private static readonly ConcurrentDictionary<Type, (string Span, string Message)> RuntimeNames = new();

    private readonly Activity? _activity;
    private readonly Activity? _previous;

    // Set only when the duration is measured.
    private readonly string? _operation;
    private readonly string? _messageType;
    private readonly long _started;

    private TelemetryScope(Activity? activity, Activity? previous, string? operation, string? messageType, long started)
    {
        _activity = activity;
        _previous = previous;
        _operation = operation;
        _messageType = messageType;
        _started = started;
    }

    private bool IsEmpty => _activity is null && _operation is null;

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
        if (!Source.HasListeners() && !Duration.Enabled)
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
        if (IsEmpty)
        {
            return;
        }

        End(exception);
        Activity.Current = _previous;
    }

    /// <summary>Ends the span when the task finishes.</summary>
    /// <param name="task">The task the operation returned.</param>
    /// <returns>The same task when it has finished, otherwise one that finishes the same way.</returns>
    public Task Complete(Task task)
    {
        if (IsEmpty)
        {
            return task;
        }

        if (task is null || task.IsCompleted)
        {
            End(ExceptionOf(task));
            Activity.Current = _previous;
            return task!;
        }

        // Unwrap rather than an async method: it copies every exception and the cancellation token across, so a
        // task from Task.WhenAll still carries all of its exceptions.
        Activity.Current = _previous;
        return task.ContinueWith(
                static (finished, state) =>
                {
                    ((TelemetryScope)state!).End(ExceptionOf(finished));
                    return finished;
                },
                this,
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
        if (IsEmpty)
        {
            return task;
        }

        if (task is null || task.IsCompleted)
        {
            End(ExceptionOf(task));
            Activity.Current = _previous;
            return task!;
        }

        Activity.Current = _previous;
        return task.ContinueWith(
                static (finished, state) =>
                {
                    ((TelemetryScope)state!).End(ExceptionOf(finished));
                    return finished;
                },
                this,
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
        if (IsEmpty)
        {
            return stream;
        }

        Activity.Current = _previous;
        return new TracedStream<TResponse>(stream, this);
    }

    private static TelemetryScope Start(string spanName, string operation, string messageType, string extraTag, object extraValue)
    {
        bool tracing = Source.HasListeners();
        bool measuring = Duration.Enabled;
        if (!tracing && !measuring)
        {
            return default;
        }

        Activity? previous = Activity.Current;
        Activity? activity = tracing ? Source.StartActivity(spanName, ActivityKind.Internal) : null;
        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag(Operation, operation);
            activity.SetTag(MessageType, messageType);
            activity.SetTag(extraTag, extraValue);
        }

        return measuring
            ? new TelemetryScope(activity, previous, operation, messageType, Stopwatch.GetTimestamp())
            : new TelemetryScope(activity, previous, null, null, 0);
    }

    // The exception awaiting the task would throw, or null for a task that succeeded.
    private static Exception? ExceptionOf(Task? finished) =>
        finished is { IsFaulted: true } ? finished.Exception!.InnerException ?? finished.Exception
        : finished is { IsCanceled: true } ? new TaskCanceledException(finished)
        : null;

    /// <summary>Ends the span and records the duration, with the failure when there is one.</summary>
    private void End(Exception? exception)
    {
        if (_operation is not null)
        {
            TagList tags = new()
            {
                { Operation, _operation },
                { MessageType, _messageType },
            };
            if (exception is not null)
            {
                tags.Add(ErrorType, exception.GetType().FullName);
            }

            Duration.Record(Stopwatch.GetElapsedTime(_started).TotalSeconds, tags);
        }

        if (_activity is null)
        {
            return;
        }

        if (exception is not null)
        {
            _activity.SetStatus(ActivityStatusCode.Error);
            if (_activity.IsAllDataRequested)
            {
                _activity.SetTag(ErrorType, exception.GetType().FullName);
                _activity.AddException(exception);
            }
        }

        _activity.Stop();
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
    private sealed class TracedStream<TResponse>(IAsyncEnumerable<TResponse> source, TelemetryScope scope)
        : IAsyncEnumerable<TResponse>, IAsyncEnumerator<TResponse>
    {
        private IAsyncEnumerator<TResponse>? _inner;
        private bool _ended;

        public TResponse Current => _inner is null ? default! : _inner.Current;

        // Enumerated once, by the DeferredEnumerable that opened it.
        public IAsyncEnumerator<TResponse> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            Activity? previous = Activity.Current;
            Activity.Current = scope._activity ?? previous;
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
            Activity.Current = scope._activity ?? previous;
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
            // Stopped early, the iterator's finally blocks run here, and what they do belongs under the stream's
            // span, which is still open. Set inside this async method, the current activity reverts for the caller
            // when it returns.
            if (!_ended)
            {
                Activity.Current = activity;
            }

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
            scope.End(exception);
        }
    }
}
