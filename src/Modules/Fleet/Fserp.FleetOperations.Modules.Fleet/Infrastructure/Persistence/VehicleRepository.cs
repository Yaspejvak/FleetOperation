using Fserp.FleetOperations.Modules.Fleet.Application.Ports;
using Fserp.FleetOperations.Modules.Fleet.Domain;
using Microsoft.EntityFrameworkCore;

namespace Fserp.FleetOperations.Modules.Fleet.Infrastructure.Persistence;

/// <summary>
/// The EF Core adapter of <see cref="IVehicleRepository"/>. Generic over the host's context so that it
/// receives the very context instance whose transaction the handler runs in; registered by type.
/// It never saves: the middleware owns the unit of work.
/// </summary>
/// <typeparam name="TContext">The host's <c>AppDbContext</c>.</typeparam>
/// <param name="database">The host's context.</param>
public sealed class VehicleRepository<TContext>(TContext database) : IVehicleRepository
    where TContext : DbContext
{
    private DbSet<Vehicle> Vehicles => database.Set<Vehicle>();

    /// <inheritdoc />
    public Task<Vehicle?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        Vehicles.FirstOrDefaultAsync(vehicle => vehicle.Id == id, cancellationToken);

    /// <inheritdoc />
    public void Add(Vehicle aggregate)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        Vehicles.Add(aggregate);
    }

    /// <inheritdoc />
    public void Remove(Vehicle aggregate)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        Vehicles.Remove(aggregate);
    }

    /// <inheritdoc />
    public Task<bool> PlateNumberExistsAsync(PlateNumber plateNumber, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plateNumber);
        return Vehicles.AsNoTracking().AnyAsync(vehicle => vehicle.PlateNumber == plateNumber, cancellationToken);
    }
}
