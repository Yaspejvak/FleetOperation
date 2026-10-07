using Fserp.FleetOperations.Modules.Fleet.Contracts;

namespace Fserp.FleetOperations.Modules.Drivers.Contracts;

/// <summary>
/// What another module may know about one driver, read from the database at the moment of the call.
/// </summary>
/// <param name="DriverId">The driver's identity.</param>
/// <param name="IsActive">Whether the operational status is Active.</param>
/// <param name="QualifiedVehicleTypes">The vehicle types the driver is qualified to operate.</param>
/// <param name="CommittedMissionId">The mission the driver is committed to, if any.</param>
public sealed record DriverSnapshot(
    Guid DriverId,
    bool IsActive,
    IReadOnlyList<VehicleType> QualifiedVehicleTypes,
    Guid? CommittedMissionId);
