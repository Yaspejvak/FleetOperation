using Fserp.FleetOperations.Modules.Fleet.Application.Ports;
using Fserp.FleetOperations.Modules.Fleet.Contracts;
using MPCore.Application.Results;

namespace Fserp.FleetOperations.Modules.Fleet.Application.Contracts;

/// <summary>
/// Fleet's implementation of <see cref="IVehicleCommitments"/>, the writing Contracts interface of
/// decision 1. Called by Operations <em>inside</em> the Assign transaction.
/// </summary>
/// <remarks>
/// <para>
/// It loads the aggregate through <see cref="IVehicleRepository"/>, calls the aggregate method and
/// <b>does not save</b>: the caller's unit of work commits the mission row, the vehicle row and their
/// audit rows together, or none of them. Calling <c>SaveChangesAsync</c> here would split one business
/// transaction in two and defeat that guarantee.
/// </para>
/// <para>
/// The repository is the write port, not the read model, so the vehicle is tracked by the caller's
/// context and its <c>xmin</c> token is carried into the caller's <c>UPDATE</c>. That is what catches a
/// concurrent assignment, status change or maintenance start (decision 2).
/// </para>
/// <para>
/// A broken rule leaves the method as a <c>BusinessRuleValidationException</c> under the <c>fleet</c>
/// domain, which both transports map to <c>422</c> with the rule's own code.
/// </para>
/// </remarks>
/// <param name="vehicles">The vehicle repository, resolved from the caller's scope.</param>
public sealed class VehicleCommitments(IVehicleRepository vehicles) : IVehicleCommitments
{
    /// <inheritdoc />
    /// <exception cref="ResultFailureException">
    /// <c>fleet/VEHICLE_NOT_FOUND</c> when no vehicle has the identity. The port returns no result, so
    /// Fleet's own not-found failure is thrown rather than a code being invented for the case; the edge
    /// maps it to the same <c>404</c> the Fleet commands produce.
    /// </exception>
    public async Task CommitToMissionAsync(
        Guid vehicleId,
        Guid missionId,
        decimal requiredCapacityKg,
        CancellationToken cancellationToken)
    {
        var vehicle = await LoadAsync(vehicleId, cancellationToken).ConfigureAwait(false);
        vehicle.CommitToMission(missionId, requiredCapacityKg);
    }

    /// <inheritdoc />
    /// <exception cref="ResultFailureException"><c>fleet/VEHICLE_NOT_FOUND</c>.</exception>
    public async Task ReleaseFromMissionAsync(Guid vehicleId, Guid missionId, CancellationToken cancellationToken)
    {
        var vehicle = await LoadAsync(vehicleId, cancellationToken).ConfigureAwait(false);
        vehicle.ReleaseFromMission(missionId);
    }

    private async Task<Domain.Vehicle> LoadAsync(Guid vehicleId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(vehicles);
        return await vehicles.GetAsync(vehicleId, cancellationToken).ConfigureAwait(false)
            ?? throw new ResultFailureException(FleetFailures.VehicleNotFound());
    }
}
