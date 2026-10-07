using Fserp.FleetOperations.Modules.Fleet.Application.Ports;
using Fserp.FleetOperations.Modules.Fleet.Application.Views;
using Fserp.FleetOperations.Modules.Fleet.Contracts;
using Fserp.FleetOperations.Modules.Fleet.Domain;
using Microsoft.EntityFrameworkCore;

namespace Fserp.FleetOperations.Modules.Fleet.Infrastructure.Persistence;

/// <summary>
/// The EF Core adapter of <see cref="IVehicleReadModel"/>. Reads without tracking, so a query can never
/// leave a change behind for a unit of work to commit. Generic over the host's context; registered by type.
/// </summary>
/// <typeparam name="TContext">The host's <c>AppDbContext</c>.</typeparam>
/// <param name="database">The host's context.</param>
public sealed class VehicleReadModel<TContext>(TContext database) : IVehicleReadModel
    where TContext : DbContext
{
    /// <inheritdoc />
    public async Task<VehicleView?> GetAsync(Guid vehicleId, CancellationToken cancellationToken)
    {
        // The value objects are stored through converters, so the row is materialized and projected in
        // memory; it is one row by primary key.
        var vehicle = await database.Set<Vehicle>()
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == vehicleId, cancellationToken)
            .ConfigureAwait(false);
        return vehicle is null ? null : VehicleView.From(vehicle);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AvailableVehicleView>> GetAvailableAsync(CancellationToken cancellationToken)
    {
        // The availability definition is written once, in the Domain (VehicleAvailability.Specification),
        // and translated to SQL here. The status columns are stored as strings through converters, so the
        // three comparisons translate; the plate and the capacity are value objects and are projected
        // after materialization. The order is by identity, which is a version 7 UUID, so the list and the
        // cache entry built from it are stable between reads.
        var vehicles = await database.Set<Vehicle>()
            .AsNoTracking()
            .Where(VehicleAvailability.Specification)
            .OrderBy(vehicle => vehicle.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return vehicles
            .Select(vehicle => new AvailableVehicleView(
                vehicle.Id,
                vehicle.PlateNumber.Value,
                vehicle.VehicleType,
                vehicle.Capacity.Kilograms))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<VehicleSnapshot?> GetSnapshotAsync(Guid vehicleId, CancellationToken cancellationToken)
    {
        // One row by primary key, straight from PostgreSQL. No cache is consulted and none is written:
        // decision 4 lists the Contracts readers under "Never cached", because Assign decides on this.
        var vehicle = await database.Set<Vehicle>()
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == vehicleId, cancellationToken)
            .ConfigureAwait(false);
        return vehicle is null
            ? null
            : new VehicleSnapshot(
                vehicle.Id,
                vehicle.VehicleType,
                vehicle.Capacity.Kilograms,
                vehicle.OperationalStatus == OperationalStatus.Active,
                vehicle.MaintenanceStatus == MaintenanceStatus.UnderMaintenance,
                vehicle.CommittedMissionId);
    }
}
