using Fserp.FleetOperations.Modules.Drivers.Application.Ports;
using Fserp.FleetOperations.Modules.Drivers.Contracts;
using Fserp.FleetOperations.Modules.Fleet.Contracts;
using MPCore.Application.Results;

namespace Fserp.FleetOperations.Modules.Drivers.Application.Contracts;

/// <summary>
/// Drivers' implementation of <see cref="IDriverCommitments"/>, the writing Contracts interface of
/// decision 1. Called by Operations <em>inside</em> the Assign transaction.
/// </summary>
/// <remarks>
/// <para>
/// It loads the aggregate through <see cref="IDriverRepository"/>, calls the aggregate method and
/// <b>does not save</b>: the caller's unit of work commits the mission row, the driver row and their audit
/// rows together, or none of them.
/// </para>
/// <para>
/// The repository is the write port, not the read model, so the driver is tracked by the caller's context
/// and its <c>xmin</c> token is carried into the caller's <c>UPDATE</c>. That is what catches a concurrent
/// assignment or deactivation (decision 2).
/// </para>
/// <para>
/// A broken rule leaves the method as a <c>BusinessRuleValidationException</c> under the <c>drivers</c>
/// domain, which both transports map to <c>422</c> with the rule's own code.
/// </para>
/// </remarks>
/// <param name="drivers">The driver repository, resolved from the caller's scope.</param>
public sealed class DriverCommitments(IDriverRepository drivers) : IDriverCommitments
{
    /// <inheritdoc />
    /// <exception cref="ResultFailureException">
    /// <c>drivers/DRIVER_NOT_FOUND</c> when no driver has the identity. The port returns no result, so the
    /// module's own not-found failure is thrown rather than a code being invented for the case; the edge
    /// maps it to the same <c>404</c> the Drivers commands produce.
    /// </exception>
    public async Task CommitToMissionAsync(
        Guid driverId,
        Guid missionId,
        VehicleType vehicleType,
        CancellationToken cancellationToken)
    {
        var driver = await LoadAsync(driverId, cancellationToken).ConfigureAwait(false);
        driver.CommitToMission(missionId, vehicleType);
    }

    /// <inheritdoc />
    /// <exception cref="ResultFailureException"><c>drivers/DRIVER_NOT_FOUND</c>.</exception>
    public async Task ReleaseFromMissionAsync(Guid driverId, Guid missionId, CancellationToken cancellationToken)
    {
        var driver = await LoadAsync(driverId, cancellationToken).ConfigureAwait(false);
        driver.ReleaseFromMission(missionId);
    }

    private async Task<Domain.Driver> LoadAsync(Guid driverId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(drivers);
        return await drivers.GetAsync(driverId, cancellationToken).ConfigureAwait(false)
            ?? throw new ResultFailureException(DriversFailures.DriverNotFound());
    }
}
