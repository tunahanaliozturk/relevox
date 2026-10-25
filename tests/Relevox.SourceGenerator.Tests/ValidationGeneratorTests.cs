using Microsoft.CodeAnalysis;

namespace Relevox.SourceGenerator.Tests;

/// <summary>
/// Relevox.FluentValidation at build time (#100, ADR 0020): its behaviour closed over the requests a validator
/// exists for, in the registration and in the message map. FluentValidation and the package are stood in for by
/// two small libraries with the same metadata names, so the rest of these tests never see them.
/// </summary>
public sealed class ValidationGeneratorTests
{
    private static readonly MetadataReference FluentValidation = GeneratorHarness.Library("FluentValidation", """
        namespace FluentValidation
        {
            public interface IValidator<in T>;
            public abstract class AbstractValidator<T> : IValidator<T>;
        }
        """);

    private static readonly MetadataReference Package = GeneratorHarness.Library("Relevox.FluentValidation", [FluentValidation], withGenerator: false, """
        using System.Threading; using System.Threading.Tasks; using Relevox;
        namespace Relevox.FluentValidation
        {
            public class ValidationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
            {
                public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken) => next(cancellationToken);
            }
        }
        """);

    // Validators in a referenced project that knows nothing of Relevox's mediator, as Clean Architecture's Application
    // project is to its Web project, and in the project itself.
    private static readonly MetadataReference Validators = GeneratorHarness.Library("Shop.Validators", [FluentValidation], withGenerator: false, """
        using Relevox;
        namespace Shop
        {
            public sealed record Register(string Name) : IRequest<int>;
            public sealed class RegisterValidator : FluentValidation.AbstractValidator<Register>;
        }
        """);

    private const string Application = """
        using System.Threading; using System.Threading.Tasks; using Microsoft.Extensions.DependencyInjection; using Relevox; using Relevox.FluentValidation;
        namespace Shop;
        public sealed class RegisterHandler : IRequestHandler<Register, int>
        {
            public Task<int> Handle(Register request, CancellationToken cancellationToken) => Task.FromResult(1);
        }
        public sealed record Transfer(decimal Amount) : IRequest;
        public sealed class TransferHandler : IRequestHandler<Transfer>
        {
            public Task Handle(Transfer request, CancellationToken cancellationToken) => Task.CompletedTask;
        }
        public sealed class TransferValidator : FluentValidation.AbstractValidator<Transfer>;
        public sealed record Look(int Id) : IRequest<string>;
        public sealed class LookHandler : IRequestHandler<Look, string>
        {
            public Task<string> Handle(Look request, CancellationToken cancellationToken) => Task.FromResult("x");
        }
        public sealed class Commands<TRequest, TResponse> : ValidationBehavior<TRequest, TResponse> where TRequest : ICommand;
        public interface ICommand;
        public static class Validation
        {
            public static RelevoxOptions AddValidation(this RelevoxOptions options) => options;
        }
        public static class Setup
        {
            public static IServiceCollection Add(IServiceCollection services) => services.AddRelevox(options => options.AddValidation());
        }
        """;

    [Fact]
    public void The_behaviour_is_closed_over_each_request_with_a_validator_and_replaces_the_open_registration()
    {
        GeneratorRun run = GeneratorHarness.Run("Subject", [FluentValidation, Package, Validators], Application);

        run.CompilationErrors.ShouldBeEmpty();
        string registration = run.Sources["RelevoxServiceCollectionExtensions.g.cs"];
        registration.ShouldContain("behavior.ImplementationType == typeof(global::Relevox.FluentValidation.ValidationBehavior<,>)");
        registration.ShouldContain("typeof(global::Relevox.IPipelineBehavior<global::Shop.Register, int>), typeof(global::Relevox.FluentValidation.ValidationBehavior<global::Shop.Register, int>), behavior.Lifetime");
        registration.ShouldContain("typeof(global::Relevox.IPipelineBehavior<global::Shop.Transfer, global::Relevox.Unit>), typeof(global::Relevox.FluentValidation.ValidationBehavior<global::Shop.Transfer, global::Relevox.Unit>), behavior.Lifetime");
        registration.ShouldNotContain("ValidationBehavior<global::Shop.Look", Case.Sensitive);

        // A derived behaviour is recognised too, and closed only where its constraint holds: neither request is an ICommand.
        registration.ShouldContain("behavior.ImplementationType == typeof(global::Shop.Commands<,>)");
        registration.ShouldNotContain("global::Shop.Commands<global::Shop.", Case.Sensitive);
    }

    [Fact]
    public void A_generic_validator_leaves_the_behaviour_open_for_every_request()
    {
        // Registered by hand, as IValidator<Rename> with RequiredFields<Rename>, or over a generic request, which the
        // build cannot see: the behaviour is not closed, so the container closes it for every request as it would
        // without the package.
        GeneratorRun run = GeneratorHarness.Run("Subject", [FluentValidation, Package, Validators], Application, """
            namespace Shop
            {
                public interface INamed;
                public sealed record Rename(string Name) : Relevox.IRequest<int>, INamed;
                public sealed class RenameHandler : Relevox.IRequestHandler<Rename, int>
                {
                    public System.Threading.Tasks.Task<int> Handle(Rename request, System.Threading.CancellationToken cancellationToken) => System.Threading.Tasks.Task.FromResult(1);
                }
                public sealed class RequiredFields<T> : FluentValidation.AbstractValidator<T> where T : INamed;
            }
            """);

        run.CompilationErrors.ShouldBeEmpty();
        string registration = run.Sources["RelevoxServiceCollectionExtensions.g.cs"];
        registration.ShouldNotContain("Validation", Case.Sensitive);
    }

    [Fact]
    public void Without_the_package_nothing_about_validation_is_generated()
    {
        GeneratorRun run = GeneratorHarness.Run("Subject", [FluentValidation, Validators], """
            using System.Threading; using System.Threading.Tasks; using Relevox;
            namespace Shop;
            public sealed class RegisterHandler : IRequestHandler<Register, int>
            {
                public Task<int> Handle(Register request, CancellationToken cancellationToken) => Task.FromResult(1);
            }
            """);

        run.CompilationErrors.ShouldBeEmpty();
        run.Sources["RelevoxServiceCollectionExtensions.g.cs"].ShouldNotContain("Validation", Case.Sensitive);
    }

    [Fact]
    public void The_message_map_lists_the_behaviour_only_under_requests_with_a_validator()
    {
        GeneratorRun run = GeneratorHarness.RunWithMessageMap("Subject", [FluentValidation, Package, Validators], Application);

        run.CompilationErrors.ShouldBeEmpty();
        string json = run.Compilation.GetTypeByMetadataName("Subject.RelevoxMessageMap")!.GetMembers("Json").OfType<IFieldSymbol>().Single().ConstantValue as string ?? "";
        json.ShouldContain("""
                  "type": "Relevox.FluentValidation.ValidationBehavior<Shop.Register, int>",
            """.ReplaceLineEndings("\n"));
        json.ShouldContain("""
                  "type": "Relevox.FluentValidation.ValidationBehavior<Shop.Transfer, Relevox.Unit>",
            """.ReplaceLineEndings("\n"));
        json.ShouldNotContain("ValidationBehavior<Shop.Look", Case.Sensitive);
        json.ShouldContain("\"unread\": []");
    }
}
