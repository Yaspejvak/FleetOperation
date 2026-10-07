using Fserp.FleetOperations.Modules.Fleet.Contracts;
using MPCore.Domain.Rules;

namespace Fserp.FleetOperations.Modules.Drivers.Domain.Rules;

/// <summary>
/// <c>DRIVER_QUALIFICATION_DUPLICATE</c>: the same vehicle type was given twice. A qualification is the
/// vehicle type only (D-4), so two for one type would be the same fact recorded twice.
/// </summary>
/// <remarks>
/// The unique index on <c>drivers.qualifications(driver_id, vehicle_type)</c> is the database backstop
/// (L-18), as the plate's unique index backstops F-2; this rule is what gives the caller the module's own
/// code instead of a constraint violation.
/// </remarks>
/// <param name="vehicleTypes">The vehicle types the driver is being registered for.</param>
public sealed class DriverQualificationDuplicateRule(IReadOnlyList<VehicleType>? vehicleTypes)
    : BusinessRule(
        DriversErrors.Domain,
        DriversErrors.QualificationDuplicate,
        DriversErrors.MessageKey(DriversErrors.QualificationDuplicate))
{
    /// <inheritdoc />
    public override bool IsBroken() =>
        vehicleTypes is not null && vehicleTypes.Distinct().Count() != vehicleTypes.Count;
}
