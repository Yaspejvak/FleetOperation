using FluentValidation;
using Fserp.FleetOperations.Modules.Operations.Application.Commands;

namespace Fserp.FleetOperations.Modules.Operations.Application.Validators;

/// <summary>Input shape of <see cref="StartMission"/>: a non-empty identity (400).</summary>
public sealed class StartMissionValidator : AbstractValidator<StartMission>
{
    /// <summary>Declares the rules.</summary>
    public StartMissionValidator()
    {
        RuleFor(command => command.MissionId).NotEmpty();
    }
}
