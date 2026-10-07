namespace Fserp.FleetOperations.Modules.Fleet.Contracts;

/// <summary>
/// What another module may know about one vehicle, read from the database at the moment of the call.
/// Never served from the cache.
/// </summary>
/// <param name="VehicleId">The vehicle's identity.</param>
/// <param name="VehicleType">The vehicle's type.</param>
/// <param name="CapacityKg">The vehicle's capacity in kilograms.</param>
/// <param name="IsActive">Whether the operational status is Active.</param>
/// <param name="IsUnderMaintenance">Whether the maintenance status is Under Maintenance.</param>
/// <param name="CommittedMissionId">The mission the vehicle is committed to, if any.</param>
public sealed record VehicleSnapshot(
    Guid VehicleId,
    VehicleType VehicleType,
    decimal CapacityKg,
    bool IsActive,
    bool IsUnderMaintenance,
    Guid? CommittedMissionId);
