using Fserp.FleetOperations.Modules.Operations.Application.Ports;
using Fserp.FleetOperations.Modules.Operations.Application.Views;
using Fserp.FleetOperations.Modules.Operations.Domain;
using Microsoft.EntityFrameworkCore;
using MPCore.Application.Querying;

namespace Fserp.FleetOperations.Modules.Operations.Infrastructure.Persistence;

/// <summary>
/// The EF Core adapter of <see cref="IMissionReadModel"/>. Reads without tracking, so a query can never
/// leave a change behind for a unit of work to commit. Generic over the host's context; registered by type.
/// </summary>
/// <typeparam name="TContext">The host's <c>AppDbContext</c>.</typeparam>
/// <param name="database">The host's context.</param>
public sealed class MissionReadModel<TContext>(TContext database) : IMissionReadModel
    where TContext : DbContext
{
    /// <inheritdoc />
    public async Task<MissionView?> GetAsync(Guid missionId, CancellationToken cancellationToken)
    {
        // The value objects are stored through converters, so the row is materialized and projected in
        // memory; it is one row by primary key.
        var mission = await database.Set<Mission>()
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == missionId, cancellationToken)
            .ConfigureAwait(false);
        return mission is null ? null : MissionView.From(mission);
    }

    /// <inheritdoc />
    public async Task<Page<MissionView>> GetActiveAsync(PageRequest page, CancellationToken cancellationToken)
    {
        // The "active" definition is written once, in the Domain (MissionActivity.ActiveSpecification), and
        // translated to SQL here; the status column is a string through the converter, so the comparisons
        // translate. The order is by identity, which is a version 7 UUID, so one caller's pages do not
        // overlap or skip rows.
        var active = database.Set<Mission>()
            .AsNoTracking()
            .Where(MissionActivity.ActiveSpecification);

        var total = await active.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await active
            .OrderBy(mission => mission.Id)
            .Skip(page.Skip)
            .Take(page.Size)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new Page<MissionView>(rows.Select(MissionView.From).ToList(), page.Number, page.Size, total);
    }
}
