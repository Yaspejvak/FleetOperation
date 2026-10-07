using MPCore.Domain.Rules;

namespace Fserp.FleetOperations.Modules.Fleet.Domain.Rules;

/// <summary>
/// <c>VEHICLE_CAPACITY_INSUFFICIENT</c>: the vehicle carries less than the mission requires
/// (docs/architecture.md, decision 1, assignment precondition 4). Both values are kilograms (X-4).
/// </summary>
/// <remarks>
/// Equal capacity is sufficient: the rule is broken only when the vehicle carries strictly less than the
/// mission asks for.
/// </remarks>
/// <param name="capacityKilograms">The vehicle's capacity in kilograms.</param>
/// <param name="requiredCapacityKilograms">The capacity the mission requires, in kilograms.</param>
public sealed class VehicleCapacityInsufficientRule(decimal capacityKilograms, decimal requiredCapacityKilograms)
    : BusinessRule(
        FleetErrors.Domain,
        FleetErrors.CapacityInsufficient,
        FleetErrors.MessageKey(FleetErrors.CapacityInsufficient))
{
    /// <inheritdoc />
    public override bool IsBroken() => capacityKilograms < requiredCapacityKilograms;
}
