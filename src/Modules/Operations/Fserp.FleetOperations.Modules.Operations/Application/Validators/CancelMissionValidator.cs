using FluentValidation;
using Fserp.FleetOperations.Modules.Operations.Application.Commands;

namespace Fserp.FleetOperations.Modules.Operations.Application.Validators;

/// <summary>
/// Input shape of <see cref="CancelMission"/>: a non-empty identity (400). There is no reason field to
/// validate (O-10).
/// </summary>
public sealed class CancelMissionValidator : AbstractValidator<CancelMission>
{
    /// <summary>Declares the rules.</summary>
    public CancelMissionValidator()
    {
        RuleFor(command => command.MissionId).NotEmpty();
    }
}
