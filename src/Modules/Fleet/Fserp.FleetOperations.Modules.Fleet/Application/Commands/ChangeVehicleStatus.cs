using Fserp.FleetOperations.Modules.Fleet.Application.Ports;
using Fserp.FleetOperations.Modules.Fleet.Application.Views;
using Fserp.FleetOperations.Modules.Fleet.Domain;
using MPCore.Application.Messaging;
using MPCore.Application.Results;
using MPCore.Audit;
using MPCore.Domain.Rules;
using MPCore.Persistence.Abstractions;

namespace Fserp.FleetOperations.Modules.Fleet.Application.Commands;

/// <summary>
/// Sets a vehicle's operational status to Active or Inactive. Maintenance is entered and left only
/// through the maintenance commands.
/// </summary>
/// <param name="VehicleId">The vehicle's identity.</param>
/// <param name="Status">The requested operational status.</param>
public sealed record ChangeVehicleStatus(Guid VehicleId, OperationalStatus Status)
    : ICommand<Result<VehicleView>>;

/// <summary>Handles <see cref="ChangeVehicleStatus"/>. One transaction, owned by the middleware.</summary>
public static class ChangeVehicleStatusHandler
{
    /// <summary>
    /// Changes the status. Unknown vehicle: <c>404</c>. Same status: <c>200</c>, no event, no audit
    /// (F-8). Committed vehicle set Inactive: <c>422 VEHICLE_HAS_MISSION_COMMITMENT</c>, with the rejected
    /// attempt recorded detached so it survives the rollback.
    /// </summary>
    /// <param name="command">The command.</param>
    /// <param name="vehicles">The vehicle repository.</param>
    /// <param name="unitOfWork">The unit of work the middleware commits; declared so the handler runs in its transaction.</param>
    /// <param name="audit">The business audit recorder.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public static async Task<Result<VehicleView>> Handle(
        ChangeVehicleStatus command,
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

        var from = vehicle.OperationalStatus;
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [FleetAudit.FromMetadata] = from.ToString(),
            [FleetAudit.ToMetadata] = command.Status.ToString(),
        };

        bool changed;
        try
        {
            // The aggregate checks its rules before it changes anything, so a refusal leaves nothing pending.
            changed = vehicle.ChangeStatus(command.Status);
        }
        catch (BusinessRuleValidationException rejected)
        {
            await audit.RecordAttemptAsync(
                    FleetAudit.Module,
                    FleetAudit.VehicleStatusChanged,
                    AuditOutcome.Rejected,
                    new AuditFailure(rejected.Rule.ErrorDomain, rejected.Rule.Code),
                    null,
                    FleetAudit.VehicleEntity,
                    vehicle.Id.ToString(),
                    metadata,
                    cancellationToken)
                .ConfigureAwait(false);
            throw;
        }

        if (changed)
        {
            await audit.RecordAsync(
                    FleetAudit.Module,
                    FleetAudit.VehicleStatusChanged,
                    FleetAudit.VehicleEntity,
                    vehicle.Id.ToString(),
                    metadata,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return Result<VehicleView>.Success(VehicleView.From(vehicle));
    }
}
