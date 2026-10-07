using MPCore.Domain.Rules;

namespace Fserp.FleetOperations.Modules.Fleet.Domain.Rules;

/// <summary>
/// <c>VEHICLE_PLATE_NUMBER_TOO_LONG</c>: the plate number is longer than
/// <see cref="PlateNumber.MaxLength"/> characters after normalization (owner decision, round 1 closure).
/// </summary>
/// <param name="normalized">The plate number, already trimmed and upper-cased.</param>
public sealed class PlateNumberTooLongRule(string? normalized)
    : BusinessRule(FleetErrors.Domain, FleetErrors.PlateNumberTooLong, FleetErrors.MessageKey(FleetErrors.PlateNumberTooLong))
{
    /// <inheritdoc />
    public override bool IsBroken() => (normalized?.Length ?? 0) > PlateNumber.MaxLength;
}
