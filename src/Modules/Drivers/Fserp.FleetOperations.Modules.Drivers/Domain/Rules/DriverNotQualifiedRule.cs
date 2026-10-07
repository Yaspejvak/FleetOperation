using Fserp.FleetOperations.Modules.Fleet.Contracts;
using MPCore.Domain.Rules;

namespace Fserp.FleetOperations.Modules.Drivers.Domain.Rules;

/// <summary>
/// <c>DRIVER_NOT_QUALIFIED</c>: the driver holds no qualification for the type of the vehicle assigned to
/// the same mission (docs/architecture.md, decision 1, assignment precondition 8).
/// </summary>
/// <remarks>
/// Checked by <c>Driver</c>, not by Operations, because the qualifications are the driver's own state and
/// are covered by the driver's concurrency token (docs/plans/drivers.md).
/// </remarks>
/// <param name="qualifications">The driver's qualifications.</param>
/// <param name="vehicleType">The type of the vehicle assigned to the mission.</param>
public sealed class DriverNotQualifiedRule(IReadOnlyCollection<Qualification>? qualifications, VehicleType vehicleType)
    : BusinessRule(DriversErrors.Domain, DriversErrors.NotQualified, DriversErrors.MessageKey(DriversErrors.NotQualified))
{
    /// <inheritdoc />
    public override bool IsBroken() =>
        qualifications is null || !qualifications.Any(qualification => qualification.VehicleType == vehicleType);
}
