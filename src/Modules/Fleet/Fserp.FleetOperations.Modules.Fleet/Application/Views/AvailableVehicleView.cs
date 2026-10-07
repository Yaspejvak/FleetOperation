using Fserp.FleetOperations.Modules.Fleet.Contracts;

namespace Fserp.FleetOperations.Modules.Fleet.Application.Views;

/// <summary>
/// One available vehicle as a caller sees it (docs/plans/fleet.md, "Views"). It carries no status field:
/// every vehicle in the list is Active, not under maintenance and not committed, so repeating that per row
/// would say nothing.
/// </summary>
/// <remarks>
/// The shape is versioned by the cache key's <c>v1</c> suffix (decision 4). Changing this record means
/// bumping that suffix, because an old serialized entry would no longer match it.
/// </remarks>
/// <param name="Id">The vehicle's identity.</param>
/// <param name="PlateNumber">The normalized plate number.</param>
/// <param name="VehicleType">The vehicle type.</param>
/// <param name="CapacityKg">The capacity in kilograms.</param>
public sealed record AvailableVehicleView(
    Guid Id,
    string PlateNumber,
    VehicleType VehicleType,
    decimal CapacityKg);
