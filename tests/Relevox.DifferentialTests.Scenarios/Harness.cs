using System.Collections;
using Microsoft.Extensions.DependencyInjection;

namespace Scenarios;

// This folder is not a project. Its files are compiled twice, once into an assembly with `global using
// MediatR;` and once into one with `global using Relevox;`. Both libraries expose the same type names, so the
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
    public static ServiceProvider Build(ServiceLifetime lifetime = ServiceLifetime.Transient, Profile profile = Profile.Plain)
    {
        ServiceCollection services = new();
        Trace trace = new();
        services.AddSingleton(trace);
        Register(services, lifetime, profile, trace);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    static partial void Register(IServiceCollection services, ServiceLifetime lifetime, Profile profile, Trace trace);
}

/// <summary>Which optional parts of each library a scenario registers.</summary>
public enum Profile
{
    /// <summary>Handlers only.</summary>
    Plain,

    /// <summary>
    /// Handlers, processors registered automatically, and pipeline behaviours: two open behaviours through the
    /// library's configuration, a
    /// constrained open behaviour, several closed ones, and one more open behaviour registered straight into
    /// the container afterwards.
    /// </summary>
    Pipeline,

    /// <summary>Handlers, with exception actions applied to every exception rather than unhandled ones.</summary>
    AllExceptions,

    /// <summary>Handlers, with notifications published through TaskWhenAllPublisher, set by type.</summary>
    WhenAll,

    /// <summary>Handlers, with notifications published through ReversePublisher, set as an instance.</summary>
    ReversePublisher,

    /// <summary>Handlers, with ReversePublisher registered with the container after the library's call.</summary>
    LatePublisher,

    /// <summary>Handlers plus one closed behaviour that is not async, and nothing else.</summary>
    SyncBehavior,

    /// <summary>
    /// Handlers, with MediatR closing generic request handlers (RegisterGenericHandlers). Relevox always closes them,
    /// so this profile only changes the MediatR side.
    /// </summary>
    GenericHandlers,

    /// <summary>
    /// An open behaviour registered with the container before the library's call, and a post-processor for
    /// Ping only, so every request carries a built-in behaviour that has work for Ping and none for the rest.
    /// </summary>
    EarlyBehavior,

    /// <summary>
    /// Behaviours that are not async, registered with the container before the library's call, and a
    /// post-processor for Ping only, so an idle built-in sits inside them for every other request.
    /// </summary>
    EarlySyncBehavior,
}

/// <summary>Runs one scenario in a fresh container and scope and describes everything observable.</summary>
public static class Run
{
    public static async Task<string> Scenario(
        Func<IServiceProvider, Task<object?>> body,
        ServiceLifetime lifetime = ServiceLifetime.Transient,
        Profile profile = Profile.Plain)
    {
        await using ServiceProvider root = Library.Build(lifetime, profile);
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

        // Every trace is compared in order. Notification handlers, processors and exception handlers all run
        // in declaration order in both libraries, so there is nothing order-insensitive left.
        IReadOnlyList<string> events = trace.Events;

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

    /// <summary>
    /// Like <see cref="Timing"/>, and also how the task ended: a task canceled by an exception and one faulted by
    /// it rethrow the same exception when awaited, so only the status tells them apart.
    /// </summary>
    public static async Task<object?> Status(Func<Task> call)
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
            return $"{task.Status} task: {exception.GetType().FullName}";
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
