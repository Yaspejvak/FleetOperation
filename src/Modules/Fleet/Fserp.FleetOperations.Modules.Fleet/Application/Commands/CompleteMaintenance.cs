using Fserp.FleetOperations.Modules.Fleet.Application.Ports;
using Fserp.FleetOperations.Modules.Fleet.Application.Views;
using MPCore.Application.Messaging;
using MPCore.Application.Results;
using MPCore.Audit;
using MPCore.Persistence.Abstractions;

namespace Fserp.FleetOperations.Modules.Fleet.Application.Commands;

/// <summary>
/// Takes a vehicle out of maintenance. Only the maintenance field changes; the operational status is
/// untouched (F-1).
/// </summary>
/// <param name="VehicleId">The vehicle's identity.</param>
public sealed record CompleteMaintenance(Guid VehicleId) : ICommand<Result<VehicleView>>;

/// <summary>Handles <see cref="CompleteMaintenance"/>. One transaction, owned by the middleware.</summary>
public static class CompleteMaintenanceHandler
{
    /// <summary>
    /// Completes maintenance. Unknown vehicle: <c>404</c>. A vehicle that is not under maintenance:
    /// <c>422 VEHICLE_NOT_UNDER_MAINTENANCE</c>. fleet.md requires no rejected-attempt audit for this
    /// command, so none is recorded; the broken rule travels as the exception the aggregate raised.
    /// </summary>
    /// <param name="command">The command.</param>
    /// <param name="vehicles">The vehicle repository.</param>
    /// <param name="unitOfWork">The unit of work the middleware commits; declared so the handler runs in its transaction.</param>
    /// <param name="audit">The business audit recorder.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public static async Task<Result<VehicleView>> Handle(
        CompleteMaintenance command,
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

        // The aggregate checks its rule before it changes anything, so a refusal leaves nothing pending.
        vehicle.CompleteMaintenance();

        await audit.RecordAsync(
                FleetAudit.Module,
                FleetAudit.MaintenanceCompleted,
                FleetAudit.VehicleEntity,
                vehicle.Id.ToString(),
                null,
                cancellationToken)
            .ConfigureAwait(false);

        return Result<VehicleView>.Success(VehicleView.From(vehicle));
    }
}
