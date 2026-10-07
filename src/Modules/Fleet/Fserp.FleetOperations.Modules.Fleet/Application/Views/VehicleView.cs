using Fserp.FleetOperations.Modules.Fleet.Contracts;
using Fserp.FleetOperations.Modules.Fleet.Domain;

namespace Fserp.FleetOperations.Modules.Fleet.Application.Views;

/// <summary>One vehicle as a caller sees it (docs/plans/fleet.md, "Views").</summary>
/// <param name="Id">The vehicle's identity.</param>
/// <param name="PlateNumber">The normalized plate number.</param>
/// <param name="VehicleType">The vehicle type.</param>
/// <param name="CapacityKg">The capacity in kilograms.</param>
/// <param name="OperationalStatus">Active or Inactive.</param>
/// <param name="MaintenanceStatus">Under maintenance or not.</param>
/// <param name="DisplayStatus">The derived status: under maintenance wins, otherwise the operational status.</param>
/// <param name="CommittedMissionId">The mission holding the vehicle, if any.</param>
public sealed record VehicleView(
    Guid Id,
    string PlateNumber,
    VehicleType VehicleType,
    decimal CapacityKg,
    OperationalStatus OperationalStatus,
    MaintenanceStatus MaintenanceStatus,
    VehicleDisplayStatus DisplayStatus,
    Guid? CommittedMissionId)
{
    /// <summary>Projects an aggregate into its view.</summary>
    /// <param name="vehicle">The vehicle.</param>
    public static VehicleView From(Vehicle vehicle)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        return new VehicleView(
            vehicle.Id,
            vehicle.PlateNumber.Value,
            vehicle.VehicleType,
            vehicle.Capacity.Kilograms,
            vehicle.OperationalStatus,
            vehicle.MaintenanceStatus,
            vehicle.DisplayStatus,
            vehicle.CommittedMissionId);
    }
}
