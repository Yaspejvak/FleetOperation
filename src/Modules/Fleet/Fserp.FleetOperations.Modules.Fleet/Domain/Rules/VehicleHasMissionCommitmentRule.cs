using MPCore.Domain.Rules;

namespace Fserp.FleetOperations.Modules.Fleet.Domain.Rules;

/// <summary>
/// <c>VEHICLE_HAS_MISSION_COMMITMENT</c>: the vehicle is committed to a mission, so it cannot be set
/// <c>Inactive</c> (F-3) or, from round 2, put under maintenance (decision 3).
/// </summary>
/// <param name="committedMissionId">The mission the vehicle is committed to, if any.</param>
public sealed class VehicleHasMissionCommitmentRule(Guid? committedMissionId)
    : BusinessRule(FleetErrors.Domain, FleetErrors.HasMissionCommitment, FleetErrors.MessageKey(FleetErrors.HasMissionCommitment))
{
    /// <inheritdoc />
    public override bool IsBroken() => committedMissionId is not null;
}
