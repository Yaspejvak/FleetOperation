using FluentValidation;
using Fserp.FleetOperations.Modules.Operations.Application.Commands;

namespace Fserp.FleetOperations.Modules.Operations.Application.Validators;

/// <summary>
/// Input shape of <see cref="AssignMission"/>: three non-empty identities (400). Whether those resources
/// may be committed needs state and is therefore not checked here: preconditions 1 to 8 belong to the
/// three aggregates.
/// </summary>
public sealed class AssignMissionValidator : AbstractValidator<AssignMission>
{
    /// <summary>Declares the rules.</summary>
    public AssignMissionValidator()
    {
        RuleFor(command => command.MissionId).NotEmpty();
        RuleFor(command => command.VehicleId).NotEmpty();
        RuleFor(command => command.DriverId).NotEmpty();
    }
}
