using Fserp.FleetOperations.Modules.Operations.Application.Ports;
using Fserp.FleetOperations.Modules.Operations.Application.Views;
using MPCore.Application.Messaging;
using MPCore.Application.Results;

namespace Fserp.FleetOperations.Modules.Operations.Application.Queries;

/// <summary>Reads one mission. Not cached: nothing in Operations is.</summary>
/// <param name="MissionId">The mission's identity.</param>
public sealed record GetMission(Guid MissionId) : IQuery<Result<MissionView>>;

/// <summary>Handles <see cref="GetMission"/>. Reads only: no unit of work, nothing published.</summary>
public static class GetMissionHandler
{
    /// <summary>Returns the mission, or <c>404 MISSION_NOT_FOUND</c>.</summary>
    /// <param name="query">The query.</param>
    /// <param name="missions">The mission read model.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<Result<MissionView>> Handle(
        GetMission query,
        IMissionReadModel missions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(missions);

        var view = await missions.GetAsync(query.MissionId, cancellationToken).ConfigureAwait(false);
        return view is null
            ? Result<MissionView>.FromFailure(OperationsFailures.MissionNotFound())
            : Result<MissionView>.Success(view);
    }
}
