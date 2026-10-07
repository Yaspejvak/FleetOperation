using MPCore.Domain.Rules;

namespace Fserp.FleetOperations.Modules.Fleet.Domain.Rules;

/// <summary>
/// <c>VEHICLE_NOT_AVAILABLE</c>: the vehicle is committed to <em>another</em> mission
/// (docs/plans/fleet.md, "Invariants"; decision 1, assignment precondition 5).
/// </summary>
/// <remarks>
/// A vehicle committed to the mission being assigned does not break this rule: committing it again to the
/// mission it already holds is a no-op, not a refusal (docs/plans/fleet.md).
/// </remarks>
/// <param name="committedMissionId">The mission the vehicle is committed to, if any.</param>
/// <param name="missionId">The mission being assigned.</param>
public sealed class VehicleNotAvailableRule(Guid? committedMissionId, Guid missionId)
    : BusinessRule(FleetErrors.Domain, FleetErrors.NotAvailable, FleetErrors.MessageKey(FleetErrors.NotAvailable))
{
    /// <inheritdoc />
    public override bool IsBroken() => committedMissionId is not null && committedMissionId != missionId;
}
