using Fserp.FleetOperations.Modules.Operations.Application.Ports;
using Fserp.FleetOperations.Modules.Operations.Application.Views;
using MPCore.Application.Messaging;
using MPCore.Application.Querying;

namespace Fserp.FleetOperations.Modules.Operations.Application.Queries;

/// <summary>
/// Reads one bounded page of the active missions: Scheduled, Assigned or InProgress (O-4). Not cached.
/// </summary>
/// <remarks>
/// The page travels as MP Core's <see cref="PageRequest"/>, which normalises itself, so the query can
/// never carry page 0 or a request for a million rows however the caller phrased it. Both transports
/// build this same record.
/// </remarks>
/// <param name="Page">The page requested, already normalised by <see cref="PageRequest"/>.</param>
public sealed record GetActiveMissions(PageRequest Page) : IQuery<Page<MissionView>>;

/// <summary>Handles <see cref="GetActiveMissions"/>. Reads only: no unit of work, nothing published.</summary>
public static class GetActiveMissionsHandler
{
    /// <summary>
    /// Returns the page. An empty page is a page, never a <c>404</c>: a fleet with no active mission is a
    /// fact about the fleet, not a missing resource.
    /// </summary>
    /// <param name="query">The query.</param>
    /// <param name="missions">The mission read model.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<Page<MissionView>> Handle(
        GetActiveMissions query,
        IMissionReadModel missions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(missions);

        return await missions.GetActiveAsync(query.Page, cancellationToken).ConfigureAwait(false);
    }
}
