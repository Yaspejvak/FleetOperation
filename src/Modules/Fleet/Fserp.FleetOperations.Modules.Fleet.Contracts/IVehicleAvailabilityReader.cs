namespace Fserp.FleetOperations.Modules.Fleet.Contracts;

/// <summary>
/// Read-only lookup of one vehicle for another module. Reads PostgreSQL, never the cache: a caller may
/// base a decision on the answer. Implemented in Fleet's <c>Application/Contracts/</c>.
/// </summary>
public interface IVehicleAvailabilityReader
{
    /// <summary>Returns the vehicle's current snapshot, or <see langword="null"/> when it does not exist.</summary>
    /// <param name="vehicleId">The vehicle's identity.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<VehicleSnapshot?> GetAsync(Guid vehicleId, CancellationToken cancellationToken);
}
