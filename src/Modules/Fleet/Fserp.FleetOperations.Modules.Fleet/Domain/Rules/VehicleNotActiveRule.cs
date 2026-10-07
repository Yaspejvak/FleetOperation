using MPCore.Domain.Rules;

namespace Fserp.FleetOperations.Modules.Fleet.Domain.Rules;

/// <summary>
/// <c>VEHICLE_NOT_ACTIVE</c>: the vehicle's operational status is not <c>Active</c>, so it cannot be
/// committed to a mission (docs/architecture.md, decision 1, assignment precondition 2).
/// </summary>
/// <param name="operationalStatus">The vehicle's current operational status.</param>
public sealed class VehicleNotActiveRule(OperationalStatus operationalStatus)
    : BusinessRule(FleetErrors.Domain, FleetErrors.NotActive, FleetErrors.MessageKey(FleetErrors.NotActive))
{
    /// <inheritdoc />
    public override bool IsBroken() => operationalStatus != OperationalStatus.Active;
}
