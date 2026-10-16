using System.Collections;
using Microsoft.Extensions.DependencyInjection;

namespace Scenarios;

// This folder is not a project. Its files are compiled twice, once into an assembly with `global using
// MediatR;` and once into one with `global using Capar;`. Both libraries expose the same type names, so the
// same source binds to either, and every scenario runs the exact same code against both. The only
// difference between the two assemblies is Library.Register, one line each.

/// <summary>Records what ran, in order, so the two libraries can be compared on more than return values.</summary>
public sealed class Trace
{
    private readonly List<string> _events = [];

    public void Add(string entry)
    {
        lock (_events)
        {
            _events.Add(entry);
        }
    }

    public IReadOnlyList<string> Events
    {
        get
        {
            lock (_events)
            {
                return [.. _events];
            }
        }
    }
}

/// <summary>Builds the container the way each library's documentation does.</summary>
public static partial class Library
{
    public static ServiceProvider Build(ServiceLifetime lifetime = ServiceLifetime.Transient)
    {
        ServiceCollection services = new();
        services.AddSingleton<Trace>();
        Register(services, lifetime);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    static partial void Register(IServiceCollection services, ServiceLifetime lifetime);
}

/// <summary>How a scenario's trace is compared.</summary>
public enum TraceOrder
{
    /// <summary>Order matters and must match exactly.</summary>
    Exact,

    /// <summary>
    /// Only the set matters. Used for notifications: MediatR runs handlers in container registration order,
    /// which follows its assembly scan and is documented as unspecified.
    /// </summary>
    Unordered,

    /// <summary>The trace is not compared, because it depends on handler order the libraries do not promise.</summary>
    Ignored,
}

/// <summary>Runs one scenario in a fresh container and scope and describes everything observable.</summary>
public static class Run
{
    public static async Task<string> Scenario(
        Func<IServiceProvider, Task<object?>> body,
        TraceOrder order = TraceOrder.Exact,
        ServiceLifetime lifetime = ServiceLifetime.Transient)
    {
        await using ServiceProvider root = Library.Build(lifetime);
        await using AsyncServiceScope scope = root.CreateAsyncScope();
        Trace trace = scope.ServiceProvider.GetRequiredService<Trace>();

        string result;
        string error = "none";
        try
        {
            result = Format(await body(scope.ServiceProvider));
        }
        catch (Exception exception)
        {
            result = "-";
            error = exception.GetType().FullName ?? exception.GetType().Name;
        }

        IEnumerable<string> events = order switch
        {
            TraceOrder.Unordered => trace.Events.Order(StringComparer.Ordinal),
            TraceOrder.Ignored => ["(not compared)"],
            _ => trace.Events,
        };

        return $"result={result} | trace=[{string.Join(", ", events)}] | error={error}";
    }

    /// <summary>
    /// Calls something that returns a task and says whether it failed before returning the task or inside it.
    /// </summary>
    /// <remarks>
    /// The difference is observable to a caller that holds the task before awaiting it, so it is part of the
    /// behaviour being compared.
    /// </remarks>
    public static async Task<object?> Timing(Func<Task> call)
    {
        Task task;
        try
        {
            task = call();
        }
        catch (Exception exception)
        {
            return $"threw synchronously: {exception.GetType().FullName}";
        }

        try
        {
            await task;
            return "completed";
        }
        catch (Exception exception)
        {
            return $"faulted task: {exception.GetType().FullName}";
        }
    }

    /// <summary>Says whether a stream failed when it was created or when it was enumerated.</summary>
    public static async Task<object?> StreamTiming<T>(Func<IAsyncEnumerable<T>> create)
    {
        IAsyncEnumerable<T> stream;
        try
        {
            stream = create();
        }
        catch (Exception exception)
        {
            return $"threw at creation: {exception.GetType().FullName}";
        }

        try
        {
            List<T> items = [];
            await foreach (T item in stream)
            {
                items.Add(item);
            }

            return items;
        }
        catch (Exception exception)
        {
            return $"threw at enumeration: {exception.GetType().FullName}";
        }
    }

    private static string Format(object? value) => value switch
    {
        null => "null",
        string text => $"\"{text}\"",
        IEnumerable sequence => "[" + string.Join(", ", sequence.Cast<object?>().Select(Format)) + "]",
        _ => $"{value.GetType().Name}:{value}",
    };
}
