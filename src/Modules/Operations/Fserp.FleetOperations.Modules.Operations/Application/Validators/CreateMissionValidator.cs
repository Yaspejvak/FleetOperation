using FluentValidation;
using Fserp.FleetOperations.Modules.Operations.Application.Commands;
using Fserp.FleetOperations.Modules.Operations.Domain;

namespace Fserp.FleetOperations.Modules.Operations.Application.Validators;

/// <summary>
/// Input shape of <see cref="CreateMission"/>, checked before the handler runs (400). It mirrors the value
/// objects' rules with the same codes and keys, so a caller sees one vocabulary; the value objects remain
/// the enforcement.
/// </summary>
public sealed class CreateMissionValidator : AbstractValidator<CreateMission>
{
    /// <summary>Declares the rules.</summary>
    public CreateMissionValidator()
    {
        // NotEmpty refuses null, empty and whitespace only, which is "empty after trimming" (O-7).
        // Origin and destination may be equal, so no comparison between them is declared.
        RuleFor(command => command.Origin)
            .NotEmpty()
            .WithErrorCode(OperationsErrors.LocationRequired)
            .WithMessage(OperationsErrors.MessageKey(OperationsErrors.LocationRequired));

        RuleFor(command => command.Destination)
            .NotEmpty()
            .WithErrorCode(OperationsErrors.LocationRequired)
            .WithMessage(OperationsErrors.MessageKey(OperationsErrors.LocationRequired));

        RuleFor(command => command.RequiredCapacityKg)
            .GreaterThan(0m)
            .WithErrorCode(OperationsErrors.RequiredCapacityMustBePositive)
            .WithMessage(OperationsErrors.MessageKey(OperationsErrors.RequiredCapacityMustBePositive));
    }
}
