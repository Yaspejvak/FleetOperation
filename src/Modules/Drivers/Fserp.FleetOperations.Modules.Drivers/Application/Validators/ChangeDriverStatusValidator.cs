using FluentValidation;
using Fserp.FleetOperations.Modules.Drivers.Application.Commands;

namespace Fserp.FleetOperations.Modules.Drivers.Application.Validators;

/// <summary>Input shape of <see cref="ChangeDriverStatus"/>: a driver identity and Active or Inactive (400).</summary>
public sealed class ChangeDriverStatusValidator : AbstractValidator<ChangeDriverStatus>
{
    /// <summary>Declares the rules.</summary>
    public ChangeDriverStatusValidator()
    {
        RuleFor(command => command.DriverId).NotEmpty();

        // OperationalStatus has exactly Active and Inactive; zero (absent) is not a member.
        RuleFor(command => command.Status).IsInEnum();
    }
}
