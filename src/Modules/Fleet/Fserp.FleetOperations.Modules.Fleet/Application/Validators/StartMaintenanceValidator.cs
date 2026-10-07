using FluentValidation;
using Fserp.FleetOperations.Modules.Fleet.Application.Commands;

namespace Fserp.FleetOperations.Modules.Fleet.Application.Validators;

/// <summary>Input shape of <see cref="StartMaintenance"/>: a non-empty identity (400).</summary>
public sealed class StartMaintenanceValidator : AbstractValidator<StartMaintenance>
{
    /// <summary>Declares the rules.</summary>
    public StartMaintenanceValidator()
    {
        RuleFor(command => command.VehicleId).NotEmpty();
    }
}
