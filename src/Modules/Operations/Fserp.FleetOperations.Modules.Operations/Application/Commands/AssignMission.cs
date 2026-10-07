using Fserp.FleetOperations.Modules.Drivers.Contracts;
using Fserp.FleetOperations.Modules.Fleet.Contracts;
using Fserp.FleetOperations.Modules.Operations.Application.Ports;
using Fserp.FleetOperations.Modules.Operations.Application.Views;
using MPCore.Application.Messaging;
using MPCore.Application.Results;
using MPCore.Audit;
using MPCore.Domain.Rules;
using MPCore.Persistence.Abstractions;

namespace Fserp.FleetOperations.Modules.Operations.Application.Commands;

/// <summary>
/// Assigns one vehicle and one driver to a scheduled mission. Scheduled -&gt; Assigned. There is no
/// reassignment and no unassignment (O-5).
/// </summary>
/// <param name="MissionId">The mission's identity.</param>
/// <param name="VehicleId">The vehicle's identity.</param>
/// <param name="DriverId">The driver's identity.</param>
public sealed record AssignMission(Guid MissionId, Guid VehicleId, Guid DriverId) : ICommand<Result<MissionView>>;

/// <summary>Handles <see cref="AssignMission"/>. One handler, one unit of work, one transaction.</summary>
/// <remarks>
/// <para>
/// <b>The order of the steps below is a correctness property, not a style.</b> It is
/// docs/plans/operations.md, "AssignMission: handler shape and transaction boundary", and
/// docs/architecture.md, decision 2. The transaction is opened and committed by the Wolverine middleware
/// because the handler declares <see cref="IUnitOfWork"/>; it contains the mission row, the vehicle row,
/// the driver row and their audit rows, and all of them commit or none do.
/// </para>
/// <para>
/// The rejected-attempt rows are the one write outside that transaction, by design: they are detached, so
/// a refusal is recorded precisely because the business change did not happen.
/// </para>
/// </remarks>
public static class AssignMissionHandler
{
    /// <summary>
    /// Assigns the vehicle and the driver, in the plan's seven steps.
    /// </summary>
    /// <param name="command">The command.</param>
    /// <param name="missions">The mission repository.</param>
    /// <param name="unitOfWork">The unit of work the middleware commits; declared so the handler runs in its transaction.</param>
    /// <param name="vehicleAvailability">Fleet's read-only snapshot port; it supplies the vehicle type for step 5.</param>
    /// <param name="driverEligibility">Drivers' read-only snapshot port.</param>
    /// <param name="vehicles">Fleet's commitment port; it checks preconditions 2 to 5.</param>
    /// <param name="drivers">Drivers' commitment port; it checks preconditions 6 to 8.</param>
    /// <param name="audit">The business audit recorder.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The assigned mission, or a failure descriptor for an unknown mission, vehicle or driver.</returns>
    /// <exception cref="BusinessRuleValidationException">
    /// <c>MISSION_NOT_ASSIGNABLE</c> (step 3), or any rule of <c>Vehicle.CommitToMission</c> (step 4) or
    /// <c>Driver.CommitToMission</c> (step 5). Each reaches the caller as <c>422</c> under its own domain
    /// and code, and each is recorded as a rejected attempt first.
    /// </exception>
    /// <exception cref="ResultFailureException">
    /// L-23: a commitment port's own <c>VEHICLE_NOT_FOUND</c> or <c>DRIVER_NOT_FOUND</c>, for the narrow
    /// case where the resource disappears between step 2 and steps 4 to 5. It is recorded as a rejected
    /// attempt and rethrown, so it reaches the caller as <c>404</c> and not as a <c>500</c>.
    /// </exception>
    public static async Task<Result<MissionView>> Handle(
        AssignMission command,
        IMissionRepository missions,
        IUnitOfWork unitOfWork,
        IVehicleAvailabilityReader vehicleAvailability,
        IDriverEligibilityReader driverEligibility,
        IVehicleCommitments vehicles,
        IDriverCommitments drivers,
        IBusinessAuditRecorder audit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(missions);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(vehicleAvailability);
        ArgumentNullException.ThrowIfNull(driverEligibility);
        ArgumentNullException.ThrowIfNull(vehicles);
        ArgumentNullException.ThrowIfNull(drivers);
        ArgumentNullException.ThrowIfNull(audit);

        // Step 1. Unknown mission: 404, nothing changed. L-22: nothing is audited here — a request naming
        // a mission that does not exist was never an attempt on a mission, and there is no entity id to
        // record it against.
        var mission = await missions.GetAsync(command.MissionId, cancellationToken).ConfigureAwait(false);
        if (mission is null)
        {
            return Result<MissionView>.FromFailure(OperationsFailures.MissionNotFound());
        }

        var metadata = OperationsAudit.Assignment(command.VehicleId, command.DriverId);

        // Step 2. The two read-only snapshots, from PostgreSQL and never from the cache (decision 4). The
        // vehicle snapshot supplies the vehicle type step 5 needs. From here on every refusal is audited
        // as a rejected attempt against the mission (L-22).
        var vehicle = await vehicleAvailability.GetAsync(command.VehicleId, cancellationToken).ConfigureAwait(false);
        if (vehicle is null)
        {
            var failure = OperationsFailures.VehicleNotFound();
            await MissionAuditing
                .RecordRejectedAsync(audit, OperationsAudit.MissionAssigned, failure, mission.Id, metadata, cancellationToken)
                .ConfigureAwait(false);
            return Result<MissionView>.FromFailure(failure);
        }

        var driver = await driverEligibility.GetAsync(command.DriverId, cancellationToken).ConfigureAwait(false);
        if (driver is null)
        {
            var failure = OperationsFailures.DriverNotFound();
            await MissionAuditing
                .RecordRejectedAsync(audit, OperationsAudit.MissionAssigned, failure, mission.Id, metadata, cancellationToken)
                .ConfigureAwait(false);
            return Result<MissionView>.FromFailure(failure);
        }

        try
        {
            // Step 3. Precondition 1, checked by the aggregate that owns the mission's state.
            mission.Assign(command.VehicleId, command.DriverId);

            // Step 4. Preconditions 2 to 5, checked by the Vehicle against the row this transaction writes.
            await vehicles
                .CommitToMissionAsync(command.VehicleId, mission.Id, mission.RequiredCapacity.Kilograms, cancellationToken)
                .ConfigureAwait(false);

            // Step 5. Preconditions 6 to 8, checked by the Driver, with the vehicle type from step 2's snapshot.
            await drivers
                .CommitToMissionAsync(command.DriverId, mission.Id, vehicle.VehicleType, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (BusinessRuleValidationException rejected)
        {
            // A broken rule in steps 3 to 5 rolls everything back. The attempt is recorded detached, with
            // the rule's own domain and code, so the record survives that rollback.
            await MissionAuditing
                .RecordRejectedAsync(audit, OperationsAudit.MissionAssigned, rejected, mission.Id, metadata, cancellationToken)
                .ConfigureAwait(false);
            throw;
        }
        catch (ResultFailureException missing)
        {
            // L-23: the resource disappeared between step 2 and steps 4 to 5. The port raises its own
            // module's not-found failure; letting it through keeps the answer a 404 rather than a 500.
            await MissionAuditing
                .RecordRejectedAsync(audit, OperationsAudit.MissionAssigned, missing.Failure, mission.Id, metadata, cancellationToken)
                .ConfigureAwait(false);
            throw;
        }

        // Step 6. The successful action joins the same transaction.
        await audit.RecordAsync(
                OperationsAudit.Module,
                OperationsAudit.MissionAssigned,
                OperationsAudit.MissionEntity,
                mission.Id.ToString(),
                metadata,
                cancellationToken)
            .ConfigureAwait(false);

        // Step 7. The middleware commits: mission row, vehicle row (xmin-checked), driver row
        // (xmin-checked), audit rows. A lost race surfaces there as a concurrency exception or a unique
        // violation on one of the two partial indexes, and reaches the caller as 409.
        return Result<MissionView>.Success(MissionView.From(mission));
    }
}
