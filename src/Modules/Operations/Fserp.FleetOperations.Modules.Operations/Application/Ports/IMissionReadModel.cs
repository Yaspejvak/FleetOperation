using Fserp.FleetOperations.Modules.Operations.Application.Views;
using MPCore.Application.Querying;

namespace Fserp.FleetOperations.Modules.Operations.Application.Ports;

/// <summary>
/// The read side of the Operations module: returns views, never aggregates, and never tracks or writes.
/// Implemented in Infrastructure. Reads PostgreSQL directly; nothing in this module is cached.
/// </summary>
public interface IMissionReadModel
{
    /// <summary>One mission, or <see langword="null"/> when no mission has the identity.</summary>
    /// <param name="missionId">The mission's identity.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<MissionView?> GetAsync(Guid missionId, CancellationToken cancellationToken);

    /// <summary>
    /// One bounded page of the active missions: Scheduled, Assigned or InProgress (O-4), ordered by
    /// identity so the pages of one request are stable.
    /// </summary>
    /// <param name="page">The normalized page request.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<Page<MissionView>> GetActiveAsync(PageRequest page, CancellationToken cancellationToken);
}
