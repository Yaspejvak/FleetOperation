using MPCore.Domain.Rules;

namespace Fserp.FleetOperations.Modules.Drivers.Domain.Rules;

/// <summary>
/// <c>DRIVER_HAS_MISSION_COMMITMENT</c>: the driver is committed to a mission, so it cannot be set
/// <c>Inactive</c> (D-2, the same rule as F-3 for a vehicle).
/// </summary>
/// <param name="committedMissionId">The mission the driver is committed to, if any.</param>
public sealed class DriverHasMissionCommitmentRule(Guid? committedMissionId)
    : BusinessRule(
        DriversErrors.Domain,
        DriversErrors.HasMissionCommitment,
        DriversErrors.MessageKey(DriversErrors.HasMissionCommitment))
{
    /// <inheritdoc />
    public override bool IsBroken() => committedMissionId is not null;
}
