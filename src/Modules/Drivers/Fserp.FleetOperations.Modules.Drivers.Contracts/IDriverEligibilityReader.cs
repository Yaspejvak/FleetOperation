namespace Fserp.FleetOperations.Modules.Drivers.Contracts;

/// <summary>
/// Read-only lookup of one driver for another module. Reads PostgreSQL, never a cache. Implemented in
/// Drivers' <c>Application/Contracts/</c>.
/// </summary>
public interface IDriverEligibilityReader
{
    /// <summary>Returns the driver's current snapshot, or <see langword="null"/> when it does not exist.</summary>
    /// <param name="driverId">The driver's identity.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<DriverSnapshot?> GetAsync(Guid driverId, CancellationToken cancellationToken);
}
