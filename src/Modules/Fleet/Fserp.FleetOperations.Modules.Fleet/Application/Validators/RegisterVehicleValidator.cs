using FluentValidation;
using Fserp.FleetOperations.Modules.Fleet.Application.Commands;
using Fserp.FleetOperations.Modules.Fleet.Domain;

namespace Fserp.FleetOperations.Modules.Fleet.Application.Validators;

/// <summary>
/// Input shape of <see cref="RegisterVehicle"/>, checked before the handler runs (400). It mirrors the
/// value objects' rules with the same codes and keys, so a caller sees one vocabulary; the value objects
/// remain the enforcement. Plate uniqueness needs state and is the handler's check, not this one's.
/// </summary>
public sealed class RegisterVehicleValidator : AbstractValidator<RegisterVehicle>
{
    /// <summary>Declares the rules.</summary>
    public RegisterVehicleValidator()
    {
        // NotEmpty refuses null, empty and whitespace only, which is "empty after trimming".
        RuleFor(command => command.PlateNumber)
            .NotEmpty()
            .WithErrorCode(FleetErrors.PlateNumberRequired)
            .WithMessage(FleetErrors.MessageKey(FleetErrors.PlateNumberRequired));

        // Measured on the normalized value, as PlateNumber measures it, so surrounding whitespace is not
        // counted and the validator and the value object agree on what 16 characters means.
        RuleFor(command => command.PlateNumber)
            .Must(static plate => PlateNumber.Normalize(plate).Length <= PlateNumber.MaxLength)
            .WithErrorCode(FleetErrors.PlateNumberTooLong)
            .WithMessage(FleetErrors.MessageKey(FleetErrors.PlateNumberTooLong));

        // Zero is not a member of VehicleType, so an absent value is refused too.
        RuleFor(command => command.VehicleType).IsInEnum();

        RuleFor(command => command.CapacityKg)
            .GreaterThan(0m)
            .WithErrorCode(FleetErrors.CapacityMustBePositive)
            .WithMessage(FleetErrors.MessageKey(FleetErrors.CapacityMustBePositive));
    }
}
