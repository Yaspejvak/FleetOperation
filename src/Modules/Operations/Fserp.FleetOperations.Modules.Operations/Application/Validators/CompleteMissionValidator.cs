using FluentValidation;
using Fserp.FleetOperations.Modules.Operations.Application.Commands;

namespace Fserp.FleetOperations.Modules.Operations.Application.Validators;

/// <summary>Input shape of <see cref="CompleteMission"/>: a non-empty identity (400).</summary>
public sealed class CompleteMissionValidator : AbstractValidator<CompleteMission>
{
    /// <summary>Declares the rules.</summary>
    public CompleteMissionValidator()
    {
        RuleFor(command => command.MissionId).NotEmpty();
    }
}
