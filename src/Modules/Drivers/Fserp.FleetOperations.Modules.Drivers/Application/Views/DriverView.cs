using Fserp.FleetOperations.Modules.Drivers.Domain;
using Fserp.FleetOperations.Modules.Fleet.Contracts;

namespace Fserp.FleetOperations.Modules.Drivers.Application.Views;

/// <summary>
/// One driver as a caller sees it. The field list is lead decision L-16: the plan names the view but not
/// its fields, and these mirror Fleet's <c>VehicleView</c>.
/// </summary>
/// <param name="Id">The driver's identity.</param>
/// <param name="FullName">The normalized full name.</param>
/// <param name="OperationalStatus">Active or Inactive.</param>
/// <param name="QualifiedVehicleTypes">The vehicle types the driver may operate, ordered by the type.</param>
/// <param name="CommittedMissionId">The mission holding the driver, if any.</param>
public sealed record DriverView(
    Guid Id,
    string FullName,
    OperationalStatus OperationalStatus,
    IReadOnlyList<VehicleType> QualifiedVehicleTypes,
    Guid? CommittedMissionId)
{
    /// <summary>Projects an aggregate into its view.</summary>
    /// <param name="driver">The driver.</param>
    /// <remarks>
    /// The qualification types are ordered by the type itself, so two reads of an unchanged driver answer
    /// the same list. The plan names no order, and a child row's insertion order is not one.
    /// </remarks>
    public static DriverView From(Driver driver)
    {
        ArgumentNullException.ThrowIfNull(driver);
        return new DriverView(
            driver.Id,
            driver.FullName.Value,
            driver.OperationalStatus,
            driver.Qualifications.Select(qualification => qualification.VehicleType).Order().ToList(),
            driver.CommittedMissionId);
    }
}
