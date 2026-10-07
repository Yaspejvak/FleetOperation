using Fserp.FleetOperations.Modules.Fleet.Contracts;
using MPCore.Domain.Rules;

namespace Fserp.FleetOperations.Modules.Drivers.Domain.Rules;

/// <summary>
/// <c>DRIVER_QUALIFICATION_REQUIRED</c>: a driver is registered with at least one qualification (D-5).
/// </summary>
/// <param name="vehicleTypes">The vehicle types the driver is being registered for.</param>
public sealed class DriverQualificationRequiredRule(IReadOnlyList<VehicleType>? vehicleTypes)
    : BusinessRule(
        DriversErrors.Domain,
        DriversErrors.QualificationRequired,
        DriversErrors.MessageKey(DriversErrors.QualificationRequired))
{
    /// <inheritdoc />
    public override bool IsBroken() => vehicleTypes is null || vehicleTypes.Count == 0;
}
