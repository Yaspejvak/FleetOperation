using FluentValidation;
using Fserp.FleetOperations.Modules.Drivers.Application.Commands;
using Fserp.FleetOperations.Modules.Drivers.Domain;

namespace Fserp.FleetOperations.Modules.Drivers.Application.Validators;

/// <summary>
/// Input shape of <see cref="RegisterDriver"/>, checked before the handler runs (400). It mirrors the
/// domain's rules with the same codes and keys, so a caller sees one vocabulary; the value object and the
/// aggregate remain the enforcement.
/// </summary>
public sealed class RegisterDriverValidator : AbstractValidator<RegisterDriver>
{
    /// <summary>Declares the rules.</summary>
    public RegisterDriverValidator()
    {
        // NotEmpty refuses null, empty and whitespace only, which is "empty after trimming".
        RuleFor(command => command.FullName)
            .NotEmpty()
            .WithErrorCode(DriversErrors.NameRequired)
            .WithMessage(DriversErrors.MessageKey(DriversErrors.NameRequired));

        // L-17: the 128-character cap is input shape and a column bound, not an invariant, so it carries
        // no DRIVER_* rule code. It is measured on the trimmed value, as DriverName.Normalize measures it
        // and as the full_name column stores it, so the three agree on what 128 characters means.
        RuleFor(command => DriverName.Normalize(command.FullName))
            .MaximumLength(DriverName.MaxLength)
            .OverridePropertyName(nameof(RegisterDriver.FullName));

        RuleFor(command => command.VehicleTypes)
            .NotEmpty()
            .WithErrorCode(DriversErrors.QualificationRequired)
            .WithMessage(DriversErrors.MessageKey(DriversErrors.QualificationRequired));

        RuleFor(command => command.VehicleTypes)
            .Must(static types => types is null || types.Distinct().Count() == types.Count)
            .WithErrorCode(DriversErrors.QualificationDuplicate)
            .WithMessage(DriversErrors.MessageKey(DriversErrors.QualificationDuplicate));

        // Zero is not a member of VehicleType, so an absent value is refused too. The collection rule
        // reports the offending index in its field path.
        RuleForEach(command => command.VehicleTypes).IsInEnum();
    }
}
