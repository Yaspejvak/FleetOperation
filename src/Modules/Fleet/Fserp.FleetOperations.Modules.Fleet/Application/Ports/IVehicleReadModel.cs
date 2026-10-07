using Fserp.FleetOperations.Modules.Fleet.Application.Views;
using Fserp.FleetOperations.Modules.Fleet.Contracts;

namespace Fserp.FleetOperations.Modules.Fleet.Application.Ports;

/// <summary>
/// The read side of the Fleet module: returns views, never aggregates, and never tracks or writes.
/// Implemented in Infrastructure. Reads PostgreSQL directly; nothing here is cached.
/// </summary>
public interface IVehicleReadModel
{
    /// <summary>One vehicle, or <see langword="null"/> when no vehicle has the identity.</summary>
    /// <param name="vehicleId">The vehicle's identity.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<VehicleView?> GetAsync(Guid vehicleId, CancellationToken cancellationToken);

    /// <summary>
    /// Every available vehicle: <c>Active</c>, not under maintenance and committed to no mission
    /// (docs/plans/fleet.md, "Available"). No filter and no paging (F-7).
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <remarks>
    /// This is the database read behind the cached list; the adapter is the one place the availability
    /// predicate is written. The query handler reaches it through the cache, never directly.
    /// </remarks>
    Task<IReadOnlyList<AvailableVehicleView>> GetAvailableAsync(CancellationToken cancellationToken);

    /// <summary>
    /// What another module may know about one vehicle, or <see langword="null"/> when no vehicle has the
    /// identity. Read from PostgreSQL at the moment of the call.
    /// </summary>
    /// <param name="vehicleId">The vehicle's identity.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <remarks>
    /// Behind <c>IVehicleAvailabilityReader</c>. Decision 4 lists the Contracts readers under "Never
    /// cached", so this read goes to the database every time: a caller may base an assignment on it.
    /// </remarks>
    Task<VehicleSnapshot?> GetSnapshotAsync(Guid vehicleId, CancellationToken cancellationToken);
}
