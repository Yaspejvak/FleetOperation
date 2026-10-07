using Fserp.FleetOperations.Modules.Operations.Application.Ports;
using Fserp.FleetOperations.Modules.Operations.Application.Views;
using Fserp.FleetOperations.Modules.Operations.Domain;
using MPCore.Application.Messaging;
using MPCore.Application.Results;
using MPCore.Audit;
using MPCore.Domain.Rules;
using MPCore.Persistence.Abstractions;

namespace Fserp.FleetOperations.Modules.Operations.Application.Commands;

/// <summary>
/// Fixes the time a mission is scheduled for. Draft -&gt; Scheduled, once (O-1).
/// </summary>
/// <remarks>
/// The scheduled time is a required input, checked by <c>ScheduleMissionValidator</c>. There is no rule
/// that it lies in the future (O-2) and a mission may start before it (O-6); neither this handler nor the
/// aggregate reads a clock to compare it with.
/// </remarks>
/// <param name="MissionId">The mission's identity.</param>
/// <param name="ScheduledAt">The time the mission is scheduled for.</param>
public sealed record ScheduleMission(Guid MissionId, DateTimeOffset ScheduledAt) : ICommand<Result<MissionView>>;

/// <summary>Handles <see cref="ScheduleMission"/>. One transaction, owned by the middleware.</summary>
public static class ScheduleMissionHandler
{
    /// <summary>
    /// Schedules the mission. Unknown mission: <c>404</c>. A mission that is not Draft:
    /// <c>422 MISSION_INVALID_TRANSITION</c>, with the rejected attempt recorded detached so the record
    /// survives the rollback.
    /// </summary>
    /// <param name="command">The command.</param>
    /// <param name="missions">The mission repository.</param>
    /// <param name="unitOfWork">The unit of work the middleware commits; declared so the handler runs in its transaction.</param>
    /// <param name="audit">The business audit recorder.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public static async Task<Result<MissionView>> Handle(
        ScheduleMission command,
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

        var metadata = OperationsAudit.Transition(mission.Status, MissionStatus.Scheduled);
        try
        {
            // The aggregate checks the edge before it changes anything, so a refusal leaves nothing pending.
            mission.Schedule(command.ScheduledAt);
        }
        catch (BusinessRuleValidationException rejected)
        {
            await MissionAuditing
                .RecordRejectedAsync(audit, OperationsAudit.MissionScheduled, rejected, mission.Id, metadata, cancellationToken)
                .ConfigureAwait(false);
            throw;
        }

        await audit.RecordAsync(
                OperationsAudit.Module,
                OperationsAudit.MissionScheduled,
                OperationsAudit.MissionEntity,
                mission.Id.ToString(),
                metadata,
                cancellationToken)
            .ConfigureAwait(false);

        return Result<MissionView>.Success(MissionView.From(mission));
    }
}
