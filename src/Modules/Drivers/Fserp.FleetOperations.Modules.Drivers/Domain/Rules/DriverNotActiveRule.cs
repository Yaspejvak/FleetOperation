using MPCore.Domain.Rules;

namespace Fserp.FleetOperations.Modules.Drivers.Domain.Rules;

/// <summary>
/// <c>DRIVER_NOT_ACTIVE</c>: the driver's operational status is not <c>Active</c>, so it cannot be
/// committed to a mission (docs/architecture.md, decision 1, assignment precondition 6).
/// </summary>
/// <param name="operationalStatus">The driver's current operational status.</param>
public sealed class DriverNotActiveRule(OperationalStatus operationalStatus)
    : BusinessRule(DriversErrors.Domain, DriversErrors.NotActive, DriversErrors.MessageKey(DriversErrors.NotActive))
{
    /// <inheritdoc />
    public override bool IsBroken() => operationalStatus != OperationalStatus.Active;
}
