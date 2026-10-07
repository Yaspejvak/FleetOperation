using Fserp.FleetOperations.Modules.Operations.Application.Ports;
using Fserp.FleetOperations.Modules.Operations.Application.Views;
using Fserp.FleetOperations.Modules.Operations.Domain;
using MPCore.Application.Messaging;
using MPCore.Application.Results;
using MPCore.Audit;
using MPCore.Domain.Rules;
using MPCore.Persistence.Abstractions;

namespace Fserp.FleetOperations.Modules.Operations.Application.Commands;

/// <summary>Starts a mission. Assigned -&gt; InProgress. The route value is the whole input.</summary>
/// <param name="MissionId">The mission's identity.</param>
public sealed record StartMission(Guid MissionId) : ICommand<Result<MissionView>>;

/// <summary>Handles <see cref="StartMission"/>. One transaction, owned by the middleware.</summary>
public static class StartMissionHandler
{
    /// <summary>
    /// Starts the mission. Unknown mission: <c>404</c>. A mission that is not Assigned:
    /// <c>422 MISSION_INVALID_TRANSITION</c>, with the rejected attempt recorded detached.
    /// </summary>
    /// <remarks>
    /// Start touches neither Fleet nor Drivers (docs/plans/operations.md), so this handler declares neither
    /// commitment port: the vehicle and the driver stay committed to the mission that is now running.
    /// </remarks>
    /// <param name="command">The command.</param>
    /// <param name="missions">The mission repository.</param>
    /// <param name="unitOfWork">The unit of work the middleware commits; declared so the handler runs in its transaction.</param>
    /// <param name="audit">The business audit recorder.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public static async Task<Result<MissionView>> Handle(
        StartMission command,
        IMissionRepository missions,
        IUnitOfWork unitOfWork,
        IBusinessAuditRecorder audit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(missions);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(audit);

        var mission = await missions.GetAsync(command.MissionId, cancellationToken).ConfigureAwait(false);
        if (mission is null)
        {
            return Result<MissionView>.FromFailure(OperationsFailures.MissionNotFound());
        }

        var metadata = OperationsAudit.Transition(mission.Status, MissionStatus.InProgress);
        try
        {
            mission.Start();
        }
        catch (BusinessRuleValidationException rejected)
        {
            await MissionAuditing
                .RecordRejectedAsync(audit, OperationsAudit.MissionStarted, rejected, mission.Id, metadata, cancellationToken)
                .ConfigureAwait(false);
            throw;
        }

        await audit.RecordAsync(
                OperationsAudit.Module,
                OperationsAudit.MissionStarted,
                OperationsAudit.MissionEntity,
                mission.Id.ToString(),
                metadata,
                cancellationToken)
            .ConfigureAwait(false);

        return Result<MissionView>.Success(MissionView.From(mission));
    }
}
