using Fserp.FleetOperations.Modules.Fleet.Application.Ports;
using Fserp.FleetOperations.Modules.Fleet.Application.Views;
using MPCore.Application.Messaging;
using MPCore.Application.Results;
using MPCore.Audit;
using MPCore.Domain.Rules;
using MPCore.Persistence.Abstractions;

namespace Fserp.FleetOperations.Modules.Fleet.Application.Commands;

/// <summary>
/// Puts a vehicle under maintenance. Maintenance carries no data (F-4), so the vehicle's identity is the
/// whole command.
/// </summary>
/// <param name="VehicleId">The vehicle's identity.</param>
public sealed record StartMaintenance(Guid VehicleId) : ICommand<Result<VehicleView>>;

/// <summary>Handles <see cref="StartMaintenance"/>. One transaction, owned by the middleware.</summary>
public static class StartMaintenanceHandler
{
    /// <summary>
    /// Starts maintenance. Unknown vehicle: <c>404</c>. Already under maintenance or committed to a
    /// mission: <c>422</c> under the rule's own code, with the rejected attempt recorded detached so the
    /// record survives the rollback (docs/plans/fleet.md, "Business audit").
    /// </summary>
    /// <param name="command">The command.</param>
    /// <param name="vehicles">The vehicle repository.</param>
    /// <param name="unitOfWork">The unit of work the middleware commits; declared so the handler runs in its transaction.</param>
    /// <param name="audit">The business audit recorder.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public static async Task<Result<VehicleView>> Handle(
        StartMaintenance command,
        IVehicleRepository vehicles,
        IUnitOfWork unitOfWork,
        IBusinessAuditRecorder audit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(vehicles);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(audit);

        var vehicle = await vehicles.GetAsync(command.VehicleId, cancellationToken).ConfigureAwait(false);
        if (vehicle is null)
        {
            return Result<VehicleView>.FromFailure(FleetFailures.VehicleNotFound());
        }

        try
        {
            // The aggregate checks its rules before it changes anything, so a refusal leaves nothing pending.
            vehicle.StartMaintenance();
        }
        catch (BusinessRuleValidationException rejected)
        {
            await audit.RecordAttemptAsync(
                    FleetAudit.Module,
                    FleetAudit.MaintenanceStarted,
                    AuditOutcome.Rejected,
                    new AuditFailure(rejected.Rule.ErrorDomain, rejected.Rule.Code),
                    null,
                    FleetAudit.VehicleEntity,
                    vehicle.Id.ToString(),
                    null,
                    cancellationToken)
                .ConfigureAwait(false);
            throw;
        }

        await audit.RecordAsync(
                FleetAudit.Module,
                FleetAudit.MaintenanceStarted,
                FleetAudit.VehicleEntity,
                vehicle.Id.ToString(),
                null,
                cancellationToken)
            .ConfigureAwait(false);

        return Result<VehicleView>.Success(VehicleView.From(vehicle));
    }
}
