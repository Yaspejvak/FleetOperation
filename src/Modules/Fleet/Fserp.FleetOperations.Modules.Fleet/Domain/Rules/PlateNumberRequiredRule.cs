using MPCore.Domain.Rules;

namespace Fserp.FleetOperations.Modules.Fleet.Domain.Rules;

/// <summary><c>VEHICLE_PLATE_NUMBER_REQUIRED</c>: the plate number is empty after normalization.</summary>
/// <param name="normalized">The plate number, already trimmed and upper-cased.</param>
public sealed class PlateNumberRequiredRule(string? normalized)
    : BusinessRule(FleetErrors.Domain, FleetErrors.PlateNumberRequired, FleetErrors.MessageKey(FleetErrors.PlateNumberRequired))
{
    /// <inheritdoc />
    public override bool IsBroken() => string.IsNullOrEmpty(normalized);
}
