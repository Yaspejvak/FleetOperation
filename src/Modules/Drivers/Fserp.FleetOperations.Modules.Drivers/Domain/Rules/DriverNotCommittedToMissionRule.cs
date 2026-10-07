using MPCore.Domain.Rules;

namespace Fserp.FleetOperations.Modules.Drivers.Domain.Rules;

/// <summary>
/// <c>DRIVER_NOT_COMMITTED_TO_MISSION</c>: the driver is not committed to the mission that asks to release
/// it (docs/plans/drivers.md, "Invariants").
/// </summary>
/// <remarks>
/// Broken both when the driver holds no mission at all and when it holds a different one, so a mission can
/// never release a driver another mission is holding.
/// </remarks>
/// <param name="committedMissionId">The mission the driver is committed to, if any.</param>
/// <param name="missionId">The mission asking to release it.</param>
public sealed class DriverNotCommittedToMissionRule(Guid? committedMissionId, Guid missionId)
    : BusinessRule(
        DriversErrors.Domain,
        DriversErrors.NotCommittedToMission,
        DriversErrors.MessageKey(DriversErrors.NotCommittedToMission))
{
    /// <inheritdoc />
    public override bool IsBroken() => committedMissionId != missionId;
}
