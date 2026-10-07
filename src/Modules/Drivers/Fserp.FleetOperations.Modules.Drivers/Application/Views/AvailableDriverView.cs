using Fserp.FleetOperations.Modules.Drivers.Domain;
using Fserp.FleetOperations.Modules.Fleet.Contracts;

namespace Fserp.FleetOperations.Modules.Drivers.Application.Views;

/// <summary>
/// One available driver as a caller sees it (lead decision L-16, mirroring Fleet's
/// <c>AvailableVehicleView</c>). It carries no status field: every driver in the list is Active and
/// committed to no mission, so repeating that per row would say nothing.
/// </summary>
/// <remarks>
/// Nothing about drivers is cached (D-6), so this record carries no shape version: it is built fresh from
/// PostgreSQL on every read and no serialized copy of an older shape can exist.
/// </remarks>
/// <param name="Id">The driver's identity.</param>
/// <param name="FullName">The normalized full name.</param>
/// <param name="QualifiedVehicleTypes">The vehicle types the driver may operate, ordered by the type.</param>
public sealed record AvailableDriverView(
    Guid Id,
    string FullName,
    IReadOnlyList<VehicleType> QualifiedVehicleTypes)
{
    /// <summary>Projects an aggregate into its view.</summary>
    /// <param name="driver">The driver.</param>
    public static AvailableDriverView From(Driver driver)
    {
        ArgumentNullException.ThrowIfNull(driver);
        return new AvailableDriverView(
            driver.Id,
            driver.FullName.Value,
            driver.Qualifications.Select(qualification => qualification.VehicleType).Order().ToList());
    }
}
