using MPCore.Domain.Rules;

namespace Fserp.FleetOperations.Modules.Fleet.Domain.Rules;

/// <summary>
/// <c>VEHICLE_NOT_COMMITTED_TO_MISSION</c>: the vehicle is not committed to the mission that asks to
/// release it (docs/plans/fleet.md, "Invariants").
/// </summary>
/// <remarks>
/// Broken both when the vehicle holds no mission at all and when it holds a different one, so a mission
/// can never release a vehicle another mission is holding.
/// </remarks>
/// <param name="committedMissionId">The mission the vehicle is committed to, if any.</param>
/// <param name="missionId">The mission asking to release it.</param>
public sealed class VehicleNotCommittedToMissionRule(Guid? committedMissionId, Guid missionId)
    : BusinessRule(
        FleetErrors.Domain,
        FleetErrors.NotCommittedToMission,
        FleetErrors.MessageKey(FleetErrors.NotCommittedToMission))
{
    /// <inheritdoc />
    public override bool IsBroken() => committedMissionId != missionId;
}
