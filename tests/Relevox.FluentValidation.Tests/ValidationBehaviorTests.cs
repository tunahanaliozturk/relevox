using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace Relevox.FluentValidation.Tests;

/// <summary>Relevox.FluentValidation (#100, ADR 0020), against the mediator generated for this project.</summary>
public sealed class ValidationBehaviorTests
{
    [Fact]
    public async Task A_valid_request_reaches_its_handler_and_an_invalid_one_throws_the_failures()
    {
        await using ServiceProvider provider = Build(static options => options.AddValidation());
        ISender sender = provider.GetRequiredService<ISender>();

        (await sender.Send(new Register("Ada"))).ShouldBe(3);
        ValidationException exception = await Should.ThrowAsync<ValidationException>(() => sender.Send(new Register("")));

        exception.Errors.ShouldHaveSingleItem().PropertyName.ShouldBe("Name");
        provider.GetRequiredService<Trace>().Events.ShouldBe(["RegisterHandler"]);
    }

    [Fact]
    public async Task Every_validator_runs_one_after_another_with_the_token_and_their_failures_are_thrown_together()
    {
        await using ServiceProvider provider = Build(static options => options.AddValidation());
        using CancellationTokenSource cancellation = new();

        ValidationException exception = await Should.ThrowAsync<ValidationException>(
            () => provider.GetRequiredService<ISender>().Send(new Transfer(0, "DE1"), cancellation.Token));

        exception.Errors.Select(static failure => failure.PropertyName).ShouldBe(["Amount", "Account"]);
        provider.GetRequiredService<Trace>().Events.ShouldBe(["TransferAccountValidator(canBeCanceled=True)"]);
    }

    [Fact]
    public async Task A_request_without_a_validator_gets_no_behaviour_at_all()
    {
        ServiceCollection services = Services(static options => options.AddValidation());
        await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        services.ShouldNotContain(static descriptor => descriptor.ServiceType == typeof(IPipelineBehavior<Look, string>));
        services.ShouldNotContain(static descriptor => descriptor.ServiceType == typeof(IPipelineBehavior<,>));
        services.ShouldContain(static descriptor => descriptor.ServiceType == typeof(IPipelineBehavior<Register, int>) && descriptor.ImplementationType == typeof(ValidationBehavior<Register, int>));
        provider.GetServices<IPipelineBehavior<Look, string>>().ShouldBeEmpty();
        (await provider.GetRequiredService<ISender>().Send(new Look(1))).ShouldBe("found");
    }

    [Fact]
    public async Task A_validator_for_a_base_type_does_not_run_for_a_derived_request()
    {
        // The container resolves IValidator<Archive>, and FluentValidation registered CommandValidator only as an
        // IValidator<Command>: the same holds for the behaviour Clean Architecture's template writes.
        await using ServiceProvider provider = Build(static options => options.AddValidation());

        (await provider.GetRequiredService<ISender>().Send(new Archive())).ShouldBe(1);
        provider.GetServices<IPipelineBehavior<Archive, int>>().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_derived_behaviour_throws_the_applications_own_exception()
    {
        await using ServiceProvider provider = Build(static options => options.AddValidation(typeof(Validation<,>)));

        InvalidRequestException exception = await Should.ThrowAsync<InvalidRequestException>(() => provider.GetRequiredService<ISender>().Send(new Register("")));

        exception.Errors.Keys.ShouldBe(["Name"]);
        provider.GetServices<IPipelineBehavior<Register, int>>().ShouldHaveSingleItem().ShouldBeOfType<Validation<Register, int>>();
    }

    [Fact]
    public async Task The_behaviour_runs_where_it_was_added_with_the_lifetime_it_was_given()
    {
        ServiceCollection services = Services(static options => options.AddOpenBehavior(typeof(Outer<,>)).AddValidation(ServiceLifetime.Scoped));
        await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using AsyncServiceScope scope = provider.CreateAsyncScope();

        await Should.ThrowAsync<ValidationException>(() => scope.ServiceProvider.GetRequiredService<ISender>().Send(new Register("")));

        scope.ServiceProvider.GetRequiredService<Trace>().Events.ShouldBe(["Outer>", "Outer<"]);
        services.Single(static descriptor => descriptor.ServiceType == typeof(IPipelineBehavior<Register, int>)).Lifetime.ShouldBe(ServiceLifetime.Scoped);
    }

    [Fact]
    public void Only_an_open_class_derived_from_the_behaviour_is_accepted()
    {
        RelevoxOptions options = new();

        Should.Throw<ArgumentException>(() => options.AddValidation(typeof(Outer<,>)));
        Should.Throw<ArgumentException>(() => options.AddValidation(typeof(Validation<Register, int>)));
    }

    private static ServiceProvider Build(Action<RelevoxOptions> configure) =>
        Services(configure).BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

    private static ServiceCollection Services(Action<RelevoxOptions> configure)
    {
        ServiceCollection services = new();
        services.AddSingleton<Trace>();
        services.AddValidatorsFromAssemblyContaining<ValidationBehaviorTests>(ServiceLifetime.Transient);
        services.AddRelevox(configure);
        return services;
    }
}
