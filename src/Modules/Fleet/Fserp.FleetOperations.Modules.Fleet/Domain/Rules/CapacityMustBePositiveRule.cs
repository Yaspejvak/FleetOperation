using MPCore.Domain.Rules;

namespace Fserp.FleetOperations.Modules.Fleet.Domain.Rules;

/// <summary><c>VEHICLE_CAPACITY_MUST_BE_POSITIVE</c>: the capacity is zero or negative kilograms (X-4).</summary>
/// <param name="kilograms">The proposed capacity in kilograms.</param>
public sealed class CapacityMustBePositiveRule(decimal kilograms)
    : BusinessRule(FleetErrors.Domain, FleetErrors.CapacityMustBePositive, FleetErrors.MessageKey(FleetErrors.CapacityMustBePositive))
{
    /// <inheritdoc />
    public override bool IsBroken() => kilograms <= 0m;
}
