using Fserp.FleetOperations.Modules.Drivers.Application.Ports;
using Fserp.FleetOperations.Modules.Drivers.Application.Views;
using Fserp.FleetOperations.Modules.Drivers.Contracts;
using Fserp.FleetOperations.Modules.Drivers.Domain;
using Microsoft.EntityFrameworkCore;

namespace Fserp.FleetOperations.Modules.Drivers.Infrastructure.Persistence;

/// <summary>
/// The EF Core adapter of <see cref="IDriverReadModel"/>. Reads without tracking, so a query can never
/// leave a change behind for a unit of work to commit. Generic over the host's context; registered by
/// type. Nothing here is cached (D-6): every read is a database read.
/// </summary>
/// <typeparam name="TContext">The host's <c>AppDbContext</c>.</typeparam>
/// <param name="database">The host's context.</param>
public sealed class DriverReadModel<TContext>(TContext database) : IDriverReadModel
    where TContext : DbContext
{
    private IQueryable<Driver> Readable =>
        database.Set<Driver>().AsNoTracking().Include(driver => driver.Qualifications);

    /// <inheritdoc />
    public async Task<DriverView?> GetAsync(Guid driverId, CancellationToken cancellationToken)
    {
        // The name is a value object stored through a converter, so the row is materialized and projected
        // in memory; it is one row by primary key with its qualification rows.
        var driver = await Readable
            .FirstOrDefaultAsync(candidate => candidate.Id == driverId, cancellationToken)
            .ConfigureAwait(false);
        return driver is null ? null : DriverView.From(driver);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AvailableDriverView>> GetAvailableAsync(CancellationToken cancellationToken)
    {
        // The availability definition is written once, in the Domain (DriverAvailability.Specification),
        // and translated to SQL here. The status column is stored as a string through a converter, so both
        // comparisons translate. The order is by identity, which is a version 7 UUID, so two reads of an
        // unchanged set answer in the same order (the reasoning of L-8, with no cache to stabilize here).
        var drivers = await Readable
            .Where(DriverAvailability.Specification)
            .OrderBy(driver => driver.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return drivers.Select(AvailableDriverView.From).ToList();
    }

    /// <inheritdoc />
    public async Task<DriverSnapshot?> GetSnapshotAsync(Guid driverId, CancellationToken cancellationToken)
    {
        // Straight from PostgreSQL. No cache is consulted and none is written: Operations decides an
        // assignment on this answer (decision 1), and nothing about drivers is cached in any case (D-6).
        var driver = await Readable
            .FirstOrDefaultAsync(candidate => candidate.Id == driverId, cancellationToken)
            .ConfigureAwait(false);
        return driver is null
            ? null
            : new DriverSnapshot(
                driver.Id,
                driver.OperationalStatus == OperationalStatus.Active,
                driver.Qualifications.Select(qualification => qualification.VehicleType).Order().ToList(),
                driver.CommittedMissionId);
    }
}
