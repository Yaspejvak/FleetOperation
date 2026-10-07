using Fserp.FleetOperations.Modules.Drivers.Contracts;
using Fserp.FleetOperations.Modules.Fleet.Contracts;
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
/// Cancels a mission. Draft, Scheduled or Assigned -&gt; Cancelled. It carries no reason (O-10), so the
/// route value is the whole input.
/// </summary>
/// <param name="MissionId">The mission's identity.</param>
public sealed record CancelMission(Guid MissionId) : ICommand<Result<MissionView>>;

/// <summary>Handles <see cref="CancelMission"/>. One transaction, owned by the middleware.</summary>
public static class CancelMissionHandler
{
    /// <summary>
    /// Cancels the mission, releasing both resources only when it was <c>Assigned</c>. Unknown mission:
    /// <c>404</c>. An <c>InProgress</c>, <c>Completed</c> or <c>Cancelled</c> mission:
    /// <c>422 MISSION_INVALID_TRANSITION</c> (O-3), with the rejected attempt recorded detached.
    /// </summary>
    /// <remarks>
    /// L-24: cancelling from <c>Draft</c> or <c>Scheduled</c> calls neither release port, because nothing
    /// was committed — releasing a resource that was never committed would itself be refused, with
    /// <c>VEHICLE_NOT_COMMITTED_TO_MISSION</c>. Only <c>Assigned</c> releases.
    /// </remarks>
    /// <param name="command">The command.</param>
    /// <param name="missions">The mission repository.</param>
    /// <param name="unitOfWork">The unit of work the middleware commits; declared so the handler runs in its transaction.</param>
    /// <param name="vehicles">Fleet's commitment port.</param>
    /// <param name="drivers">Drivers' commitment port.</param>
    /// <param name="audit">The business audit recorder.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public static async Task<Result<MissionView>> Handle(
        CancelMission command,
        IMissionRepository missions,
        IUnitOfWork unitOfWork,
        IVehicleCommitments vehicles,
        IDriverCommitments drivers,
        IBusinessAuditRecorder audit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(missions);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(vehicles);
        ArgumentNullException.ThrowIfNull(drivers);
        ArgumentNullException.ThrowIfNull(audit);

        var mission = await missions.GetAsync(command.MissionId, cancellationToken).ConfigureAwait(false);
        if (mission is null)
        {
            return Result<MissionView>.FromFailure(OperationsFailures.MissionNotFound());
        }

        var metadata = OperationsAudit.Transition(mission.Status, MissionStatus.Cancelled);
        var vehicleId = mission.AssignedVehicleId;
        var driverId = mission.AssignedDriverId;

        MissionStatus from;
        try
        {
            from = mission.Cancel();
        }
        catch (BusinessRuleValidationException rejected)
        {
            await MissionAuditing
                .RecordRejectedAsync(audit, OperationsAudit.MissionCancelled, rejected, mission.Id, metadata, cancellationToken)
                .ConfigureAwait(false);
            throw;
        }

        if (from == MissionStatus.Assigned)
        {
            await MissionResources
                .ReleaseAsync(vehicles, drivers, vehicleId, driverId, mission.Id, cancellationToken)
                .ConfigureAwait(false);
        }

        await audit.RecordAsync(
                OperationsAudit.Module,
                OperationsAudit.MissionCancelled,
                OperationsAudit.MissionEntity,
                mission.Id.ToString(),
                metadata,
                cancellationToken)
            .ConfigureAwait(false);

        return Result<MissionView>.Success(MissionView.From(mission));
    }
}
