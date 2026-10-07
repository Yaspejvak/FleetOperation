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

/// <summary>Completes a mission. InProgress -&gt; Completed, releasing the vehicle and the driver.</summary>
/// <param name="MissionId">The mission's identity.</param>
public sealed record CompleteMission(Guid MissionId) : ICommand<Result<MissionView>>;

/// <summary>Handles <see cref="CompleteMission"/>. One transaction, owned by the middleware.</summary>
public static class CompleteMissionHandler
{
    /// <summary>
    /// Completes the mission and releases both resources in the same transaction. Unknown mission:
    /// <c>404</c>. A mission that is not InProgress: <c>422 MISSION_INVALID_TRANSITION</c>, with the
    /// rejected attempt recorded detached and neither release called.
    /// </summary>
    /// <remarks>
    /// The transition is checked first, so a refused Complete never touches Fleet or Drivers. The two
    /// releases then run inside this command's own transaction (docs/plans/operations.md), which is what
    /// raises <c>VehicleReleasedFromMission</c> in Fleet and therefore evicts the available-vehicles cache.
    /// An InProgress mission always holds both resources, so both ids are present; the invariant is
    /// asserted rather than worked around, because a null here would mean the state machine was bypassed.
    /// </remarks>
    /// <param name="command">The command.</param>
    /// <param name="missions">The mission repository.</param>
    /// <param name="unitOfWork">The unit of work the middleware commits; declared so the handler runs in its transaction.</param>
    /// <param name="vehicles">Fleet's commitment port.</param>
    /// <param name="drivers">Drivers' commitment port.</param>
    /// <param name="audit">The business audit recorder.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public static async Task<Result<MissionView>> Handle(
        CompleteMission command,
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

        var metadata = OperationsAudit.Transition(mission.Status, MissionStatus.Completed);
        var vehicleId = mission.AssignedVehicleId;
        var driverId = mission.AssignedDriverId;
        try
        {
            mission.Complete();
        }
        catch (BusinessRuleValidationException rejected)
        {
            await MissionAuditing
                .RecordRejectedAsync(audit, OperationsAudit.MissionCompleted, rejected, mission.Id, metadata, cancellationToken)
                .ConfigureAwait(false);
            throw;
        }

        // Only an InProgress mission reaches this line, and an InProgress mission was assigned.
        await MissionResources
            .ReleaseAsync(vehicles, drivers, vehicleId, driverId, mission.Id, cancellationToken)
            .ConfigureAwait(false);

        await audit.RecordAsync(
                OperationsAudit.Module,
                OperationsAudit.MissionCompleted,
                OperationsAudit.MissionEntity,
                mission.Id.ToString(),
                metadata,
                cancellationToken)
            .ConfigureAwait(false);

        return Result<MissionView>.Success(MissionView.From(mission));
    }
}
