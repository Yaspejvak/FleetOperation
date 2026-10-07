using MPCore.Domain.Rules;

namespace Fserp.FleetOperations.Modules.Drivers.Domain.Rules;

/// <summary>
/// <c>DRIVER_NOT_AVAILABLE</c>: the driver is committed to <em>another</em> mission (docs/plans/drivers.md,
/// "Invariants"; decision 1, assignment precondition 7).
/// </summary>
/// <remarks>
/// A driver committed to the mission being assigned does not break this rule, exactly as the vehicle's
/// <c>VEHICLE_NOT_AVAILABLE</c> is defined: the plan words both as "committed to a different mission".
/// </remarks>
/// <param name="committedMissionId">The mission the driver is committed to, if any.</param>
/// <param name="missionId">The mission being assigned.</param>
public sealed class DriverNotAvailableRule(Guid? committedMissionId, Guid missionId)
    : BusinessRule(DriversErrors.Domain, DriversErrors.NotAvailable, DriversErrors.MessageKey(DriversErrors.NotAvailable))
{
    /// <inheritdoc />
    public override bool IsBroken() => committedMissionId is not null && committedMissionId != missionId;
}
