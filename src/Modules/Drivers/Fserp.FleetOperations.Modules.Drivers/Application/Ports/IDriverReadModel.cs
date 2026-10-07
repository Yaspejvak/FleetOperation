using Fserp.FleetOperations.Modules.Drivers.Application.Views;
using Fserp.FleetOperations.Modules.Drivers.Contracts;

namespace Fserp.FleetOperations.Modules.Drivers.Application.Ports;

/// <summary>
/// The read side of the Drivers module: returns views, never aggregates, and never tracks or writes.
/// Implemented in Infrastructure. Reads PostgreSQL directly; nothing here is cached (D-6).
/// </summary>
public interface IDriverReadModel
{
    /// <summary>One driver, or <see langword="null"/> when no driver has the identity.</summary>
    /// <param name="driverId">The driver's identity.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<DriverView?> GetAsync(Guid driverId, CancellationToken cancellationToken);

    /// <summary>
    /// Every available driver: <c>Active</c> and committed to no mission (docs/plans/drivers.md,
    /// "Available"). No vehicle-type filter and no paging (D-6).
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<IReadOnlyList<AvailableDriverView>> GetAvailableAsync(CancellationToken cancellationToken);

    /// <summary>
    /// What another module may know about one driver, or <see langword="null"/> when no driver has the
    /// identity. Read from PostgreSQL at the moment of the call, never from a cache.
    /// </summary>
    /// <param name="driverId">The driver's identity.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<DriverSnapshot?> GetSnapshotAsync(Guid driverId, CancellationToken cancellationToken);
}
