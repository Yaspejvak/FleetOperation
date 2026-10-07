using Fserp.FleetOperations.Modules.Drivers.Application.Ports;
using Fserp.FleetOperations.Modules.Drivers.Domain;
using Microsoft.EntityFrameworkCore;

namespace Fserp.FleetOperations.Modules.Drivers.Infrastructure.Persistence;

/// <summary>
/// The EF Core adapter of <see cref="IDriverRepository"/>. Generic over the host's context so that it
/// receives the very context instance whose transaction the handler runs in; registered by type.
/// It never saves: the middleware owns the unit of work.
/// </summary>
/// <typeparam name="TContext">The host's <c>AppDbContext</c>.</typeparam>
/// <param name="database">The host's context.</param>
public sealed class DriverRepository<TContext>(TContext database) : IDriverRepository
    where TContext : DbContext
{
    private DbSet<Driver> Drivers => database.Set<Driver>();

    /// <inheritdoc />
    /// <remarks>
    /// The qualifications come with the driver: <c>DRIVER_NOT_QUALIFIED</c> reads them, so a driver loaded
    /// without them would make the rule say "not qualified" about a driver that is.
    /// </remarks>
    public Task<Driver?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        Drivers
            .Include(driver => driver.Qualifications)
            .FirstOrDefaultAsync(driver => driver.Id == id, cancellationToken);

    /// <inheritdoc />
    public void Add(Driver aggregate)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        Drivers.Add(aggregate);
    }

    /// <inheritdoc />
    public void Remove(Driver aggregate)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        Drivers.Remove(aggregate);
    }
}
