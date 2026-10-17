extern alias oncapar;

using Microsoft.Extensions.DependencyInjection;
using CaparMediator = oncapar::Capar.DifferentialTests.OnCapar.CaparMediator;
using Registration = oncapar::Microsoft.Extensions.DependencyInjection.CaparServiceCollectionExtensions;
using S = oncapar::Scenarios;

namespace Capar.DifferentialTests;

/// <summary>
/// Behaviour MediatR has no equivalent for, so there is nothing to compare against: the typed overloads on
/// the generated mediator, handler lifetimes, and what happens when two projects both call AddCapar.
/// </summary>
public sealed class CaparOnlyTests
{
    [Fact]
    public async Task A_typed_overload_goes_by_the_runtime_type_not_the_static_one()
    {
        await using ServiceProvider provider = Build();
        CaparMediator mediator = provider.GetRequiredService<CaparMediator>();

        // Binds to Send(BaseQuery) at compile time. The value is a DerivedQuery, which has no handler of its
        // own; MediatR and the interface path both throw for it, and the typed path has to agree.
        S.BaseQuery query = new S.DerivedQuery();

        await Should.ThrowAsync<InvalidOperationException>(() => mediator.Send(query));
        (await mediator.Send(new S.BaseQuery())).ShouldBe("base");
    }

    [Fact]
    public async Task Singleton_handlers_are_resolved_once_and_kept()
    {
        await using ServiceProvider provider = Build(options => options.HandlerLifetime = ServiceLifetime.Singleton);
        CaparMediator mediator = provider.GetRequiredService<CaparMediator>();

        (await mediator.Send(new S.Stamp())).ShouldBe(await mediator.Send(new S.Stamp()));
    }

    [Fact]
    public async Task A_handler_the_application_registered_as_transient_is_never_kept()
    {
        // The application's own registration wins over the option, because AddCapar only adds what is missing,
        // and a kept transient handler would quietly become a shared one.
        await using ServiceProvider provider = Build(
            options => options.HandlerLifetime = ServiceLifetime.Singleton,
            services => services.AddTransient<S.StampHandler>());
        CaparMediator mediator = provider.GetRequiredService<CaparMediator>();

        (await mediator.Send(new S.Stamp())).ShouldNotBe(await mediator.Send(new S.Stamp()));
    }

    [Fact]
    public void Calling_AddCapar_twice_is_harmless()
    {
        ServiceCollection services = new();
        Registration.AddCapar(services);
        int count = services.Count;

        Registration.AddCapar(services);

        services.Count.ShouldBe(count);
    }

    [Fact]
    public void AddCapar_refuses_to_run_after_another_mediator_was_registered()
    {
        ServiceCollection services = new();
        services.AddSingleton<global::Capar.IMediator>(_ => throw new NotSupportedException());

        InvalidOperationException refused = Should.Throw<InvalidOperationException>(() => Registration.AddCapar(services));
        refused.Message.ShouldContain("Capar.Contracts");
    }

    private static ServiceProvider Build(Action<global::Capar.CaparOptions>? configure = null, Action<IServiceCollection>? before = null)
    {
        ServiceCollection services = new();
        services.AddSingleton<S.Trace>();
        before?.Invoke(services);
        Registration.AddCapar(services, configure);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }
}
