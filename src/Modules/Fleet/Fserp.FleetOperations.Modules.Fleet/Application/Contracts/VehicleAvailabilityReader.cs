using Fserp.FleetOperations.Modules.Fleet.Application.Ports;
using Fserp.FleetOperations.Modules.Fleet.Contracts;

namespace Fserp.FleetOperations.Modules.Fleet.Application.Contracts;

/// <summary>
/// Fleet's implementation of <see cref="IVehicleAvailabilityReader"/>, the read-only snapshot another
/// module may take of one vehicle (docs/architecture.md, decision 1).
/// </summary>
/// <remarks>
/// <para>
/// It reads through the module's own read port, which goes to PostgreSQL. It declares no
/// <c>ICache</c> and no <c>IReadThroughCache</c> and cannot therefore serve a cached value: decision 4
/// lists the Contracts readers under "Never cached", because Operations decides an assignment on the
/// answer and the cached available list is advisory only.
/// </para>
/// <para>
/// It is a translation, not a decision: no rule is evaluated here. Every precondition the snapshot is
/// read for is checked again by <c>Vehicle</c> itself against the row the Assign transaction writes.
/// </para>
/// </remarks>
/// <param name="vehicles">The Fleet read port.</param>
public sealed class VehicleAvailabilityReader(IVehicleReadModel vehicles) : IVehicleAvailabilityReader
{
    /// <inheritdoc />
    public Task<VehicleSnapshot?> GetAsync(Guid vehicleId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(vehicles);
        return vehicles.GetSnapshotAsync(vehicleId, cancellationToken);
    }
}
