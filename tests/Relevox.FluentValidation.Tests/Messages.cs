using FluentValidation;
using FluentValidation.Results;
using Relevox.FluentValidation;

namespace Relevox.FluentValidation.Tests;

/// <summary>What ran, in order, shared by the handlers, behaviours and validators of one container.</summary>
public sealed class Trace
{
    private readonly List<string> _events = [];

    public IReadOnlyList<string> Events => _events;

    public void Add(string entry) => _events.Add(entry);
}

/// <summary>One validator, which fails for an empty name.</summary>
public sealed record Register(string Name) : IRequest<int>;

public sealed class RegisterHandler(Trace trace) : IRequestHandler<Register, int>
{
    public Task<int> Handle(Register request, CancellationToken cancellationToken)
    {
        trace.Add("RegisterHandler");
        return Task.FromResult(request.Name.Length);
    }
}

public sealed class RegisterValidator : AbstractValidator<Register>
{
    public RegisterValidator() => RuleFor(static request => request.Name).NotEmpty();
}

/// <summary>Two validators, one of them async, and no response.</summary>
public sealed record Transfer(decimal Amount, string Account) : IRequest;

public sealed class TransferHandler(Trace trace) : IRequestHandler<Transfer>
{
    public Task Handle(Transfer request, CancellationToken cancellationToken)
    {
        trace.Add("TransferHandler");
        return Task.CompletedTask;
    }
}

public sealed class TransferAmountValidator : AbstractValidator<Transfer>
{
    public TransferAmountValidator() => RuleFor(static request => request.Amount).GreaterThan(0);
}

public sealed class TransferAccountValidator : AbstractValidator<Transfer>
{
    public TransferAccountValidator(Trace trace) =>
        RuleFor(static request => request.Account).MustAsync(async (account, cancellationToken) =>
        {
            trace.Add($"TransferAccountValidator(canBeCanceled={cancellationToken.CanBeCanceled})");
            await Task.Yield();
            return account.StartsWith("TR", StringComparison.Ordinal);
        }).WithMessage("Unknown account.");
}

/// <summary>No validator at all.</summary>
public sealed record Look(int Id) : IRequest<string>;

public sealed class LookHandler(Trace trace) : IRequestHandler<Look, string>
{
    public Task<string> Handle(Look request, CancellationToken cancellationToken)
    {
        trace.Add("LookHandler");
        return Task.FromResult("found");
    }
}

/// <summary>A validator for a base type, which the container does not hand out for the derived request.</summary>
public abstract record Command : IRequest<int>;

public sealed record Archive : Command;

public sealed class ArchiveHandler : IRequestHandler<Archive, int>
{
    public Task<int> Handle(Archive request, CancellationToken cancellationToken) => Task.FromResult(1);
}

public sealed class CommandValidator : AbstractValidator<Command>
{
    public CommandValidator() => RuleFor(static command => command).Must(static _ => false);
}

/// <summary>An exception of the application's own, as Clean Architecture's template has.</summary>
public sealed class InvalidRequestException(IEnumerable<ValidationFailure> failures) : Exception("One or more validation failures have occurred.")
{
    public IDictionary<string, string[]> Errors { get; } = failures
        .GroupBy(static failure => failure.PropertyName, static failure => failure.ErrorMessage)
        .ToDictionary(static group => group.Key, static group => group.ToArray());
}

public sealed class Validation<TRequest, TResponse>(IEnumerable<IValidator<TRequest>> validators) : ValidationBehavior<TRequest, TResponse>(validators)
    where TRequest : notnull
{
    protected override Exception CreateException(IReadOnlyList<ValidationFailure> failures) => new InvalidRequestException(failures);
}

public sealed class Outer<TRequest, TResponse>(Trace trace) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        trace.Add("Outer>");
        try
        {
            return await next(cancellationToken);
        }
        finally
        {
            trace.Add("Outer<");
        }
    }
}
