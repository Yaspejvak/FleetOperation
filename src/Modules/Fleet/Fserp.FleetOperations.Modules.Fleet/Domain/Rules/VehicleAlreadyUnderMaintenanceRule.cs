using MPCore.Domain.Rules;

namespace Fserp.FleetOperations.Modules.Fleet.Domain.Rules;

/// <summary>
/// <c>VEHICLE_ALREADY_UNDER_MAINTENANCE</c>: the vehicle is already under maintenance, so maintenance
/// cannot be started again (docs/plans/fleet.md, "Invariants").
/// </summary>
/// <remarks>
/// A hard refusal, not a no-op: unlike <c>ChangeStatus</c> (F-8), the plan lists this as a broken rule.
/// </remarks>
/// <param name="maintenanceStatus">The vehicle's current maintenance status.</param>
public sealed class VehicleAlreadyUnderMaintenanceRule(MaintenanceStatus maintenanceStatus)
    : BusinessRule(
        FleetErrors.Domain,
        FleetErrors.AlreadyUnderMaintenance,
        FleetErrors.MessageKey(FleetErrors.AlreadyUnderMaintenance))
{
    /// <inheritdoc />
    public override bool IsBroken() => maintenanceStatus == MaintenanceStatus.UnderMaintenance;
}
