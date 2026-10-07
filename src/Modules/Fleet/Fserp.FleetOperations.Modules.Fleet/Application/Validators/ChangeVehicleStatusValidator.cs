using FluentValidation;
using Fserp.FleetOperations.Modules.Fleet.Application.Commands;

namespace Fserp.FleetOperations.Modules.Fleet.Application.Validators;

/// <summary>Input shape of <see cref="ChangeVehicleStatus"/>: a vehicle identity and Active or Inactive (400).</summary>
public sealed class ChangeVehicleStatusValidator : AbstractValidator<ChangeVehicleStatus>
{
    /// <summary>Declares the rules.</summary>
    public ChangeVehicleStatusValidator()
    {
        RuleFor(command => command.VehicleId).NotEmpty();

        // OperationalStatus has exactly Active and Inactive; zero (absent) is not a member.
        RuleFor(command => command.Status).IsInEnum();
    }
}
