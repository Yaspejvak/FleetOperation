using Fserp.FleetOperations.Modules.Operations.Application.Ports;
using Fserp.FleetOperations.Modules.Operations.Domain;
using Microsoft.EntityFrameworkCore;

namespace Fserp.FleetOperations.Modules.Operations.Infrastructure.Persistence;

/// <summary>
/// The EF Core adapter of <see cref="IMissionRepository"/>. Generic over the host's context so that it
/// receives the very context instance whose transaction the handler runs in; registered by type.
/// It never saves: the middleware owns the unit of work.
/// </summary>
/// <typeparam name="TContext">The host's <c>AppDbContext</c>.</typeparam>
/// <param name="database">The host's context.</param>
public sealed class MissionRepository<TContext>(TContext database) : IMissionRepository
    where TContext : DbContext
{
    private DbSet<Mission> Missions => database.Set<Mission>();

    /// <inheritdoc />
    /// <remarks>
    /// Tracked, so the mission's <c>xmin</c> is carried into the caller's <c>UPDATE</c> and a second
    /// operator acting on the same mission loses the race at commit (decision 2).
    /// </remarks>
    public Task<Mission?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        Missions.FirstOrDefaultAsync(mission => mission.Id == id, cancellationToken);

    /// <inheritdoc />
    public void Add(Mission aggregate)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        Missions.Add(aggregate);
    }

    /// <inheritdoc />
    public void Remove(Mission aggregate)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        Missions.Remove(aggregate);
    }
}
