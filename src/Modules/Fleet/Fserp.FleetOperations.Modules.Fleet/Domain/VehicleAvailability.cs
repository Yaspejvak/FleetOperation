using System.Linq.Expressions;

namespace Fserp.FleetOperations.Modules.Fleet.Domain;

/// <summary>
/// What "available" means for a vehicle, written once (docs/plans/fleet.md, "Available"):
/// <c>OperationalStatus == Active &amp;&amp; MaintenanceStatus == NotUnderMaintenance &amp;&amp;
/// CommittedMissionId == null</c>.
/// </summary>
/// <remarks>
/// It is an expression rather than a method body so the one definition can be both translated to SQL by
/// the read model and evaluated in memory, instead of being written a second time for either. The plan
/// names two users for it: the cached list and, in a later round, the commit rule.
/// </remarks>
public static class VehicleAvailability
{
    /// <summary>The availability predicate, as a query the read model can translate.</summary>
    public static Expression<Func<Vehicle, bool>> Specification { get; } = vehicle =>
        vehicle.OperationalStatus == OperationalStatus.Active
        && vehicle.MaintenanceStatus == MaintenanceStatus.NotUnderMaintenance
        && vehicle.CommittedMissionId == null;

    private static readonly Func<Vehicle, bool> Compiled = Specification.Compile();

    /// <summary>Whether a loaded vehicle is available, by the same definition.</summary>
    /// <param name="vehicle">The vehicle.</param>
    public static bool IsAvailable(Vehicle vehicle)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        return Compiled(vehicle);
    }
}
