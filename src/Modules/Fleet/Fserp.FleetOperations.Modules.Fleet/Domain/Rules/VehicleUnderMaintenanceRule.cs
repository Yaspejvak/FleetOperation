using MPCore.Domain.Rules;

namespace Fserp.FleetOperations.Modules.Fleet.Domain.Rules;

/// <summary>
/// <c>VEHICLE_UNDER_MAINTENANCE</c>: the vehicle is under maintenance, so it cannot be committed to a
/// mission (docs/architecture.md, decision 1, assignment precondition 3).
/// </summary>
/// <remarks>
/// The mirror image of <see cref="VehicleNotUnderMaintenanceRule"/>, which refuses completing maintenance
/// on a vehicle that is not under it. Two codes, two directions, neither reusable for the other.
/// </remarks>
/// <param name="maintenanceStatus">The vehicle's current maintenance status.</param>
public sealed class VehicleUnderMaintenanceRule(MaintenanceStatus maintenanceStatus)
    : BusinessRule(FleetErrors.Domain, FleetErrors.UnderMaintenance, FleetErrors.MessageKey(FleetErrors.UnderMaintenance))
{
    /// <inheritdoc />
    public override bool IsBroken() => maintenanceStatus == MaintenanceStatus.UnderMaintenance;
}
