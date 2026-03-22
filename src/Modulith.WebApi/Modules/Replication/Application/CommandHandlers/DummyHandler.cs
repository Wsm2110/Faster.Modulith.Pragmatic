using FluentValidation;
using Modulith.DomainEventDispatcher.Contracts;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Modulith.WebApi.Modules.Replication.Application.CommandHandlers;

/// <summary>
/// 
/// </summary>
public class Dummy2CommandHandler : ICommandHandler<Dummy2Command, Result>
{
    private readonly IValidator<Dummy2Command> _validator;

    public Dummy2CommandHandler(IValidator<Dummy2Command> validator)
    {
        _validator = validator;
    }

    public async ValueTask<Result> Handle(Dummy2Command command, CancellationToken ct = default)
    {
        var validation = await _validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            return Result.Failure(validation.ToString());

        throw new NotImplementedException();
    }
}
/// <summary>
/// Represents the command parameters for the <see cref="Dummy2CommandHandler"/>.
/// </summary>
public record Dummy2Command() : ICommand<Result>;
/// <summary>
/// Provides validation rules for the <see cref="Dummy2Command"/>.
/// </summary>
internal sealed class Dummy2Validator : AbstractValidator<Dummy2Command>
{
    public Dummy2Validator()
    {
    }
}