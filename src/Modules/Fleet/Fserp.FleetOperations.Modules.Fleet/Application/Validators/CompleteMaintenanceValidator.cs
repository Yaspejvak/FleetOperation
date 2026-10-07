using FluentValidation;
using Fserp.FleetOperations.Modules.Fleet.Application.Commands;

namespace Fserp.FleetOperations.Modules.Fleet.Application.Validators;

/// <summary>Input shape of <see cref="CompleteMaintenance"/>: a non-empty identity (400).</summary>
public sealed class CompleteMaintenanceValidator : AbstractValidator<CompleteMaintenance>
{
    /// <summary>Declares the rules.</summary>
    public CompleteMaintenanceValidator()
    {
        RuleFor(command => command.VehicleId).NotEmpty();
    }
}
