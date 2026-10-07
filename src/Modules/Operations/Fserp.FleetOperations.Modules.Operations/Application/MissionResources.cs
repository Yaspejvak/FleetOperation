using Fserp.FleetOperations.Modules.Drivers.Contracts;
using Fserp.FleetOperations.Modules.Fleet.Contracts;

namespace Fserp.FleetOperations.Modules.Operations.Application;

/// <summary>
/// Releases the vehicle and the driver a mission held, through the two Contracts ports, inside the
/// caller's transaction. Written once because Complete and Cancel-from-Assigned release identically
/// (docs/plans/operations.md).
/// </summary>
public static class MissionResources
{
    /// <summary>Releases both resources, vehicle first, then driver.</summary>
    /// <param name="vehicles">Fleet's commitment port.</param>
    /// <param name="drivers">Drivers' commitment port.</param>
    /// <param name="vehicleId">The vehicle the mission held.</param>
    /// <param name="driverId">The driver the mission held.</param>
    /// <param name="missionId">The mission that held them.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <exception cref="InvalidOperationException">
    /// A mission in a status that holds resources has no id for one of them. That cannot happen through
    /// the state machine, so it is a defect and is not answered as a business failure.
    /// </exception>
    /// <remarks>
    /// Neither port saves: the caller's unit of work commits the mission row, the vehicle row, the driver
    /// row and the audit rows together, or none of them.
    /// </remarks>
    public static async Task ReleaseAsync(
        IVehicleCommitments vehicles,
        IDriverCommitments drivers,
        Guid? vehicleId,
        Guid? driverId,
        Guid missionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(vehicles);
        ArgumentNullException.ThrowIfNull(drivers);
        if (vehicleId is not { } vehicle || driverId is not { } driver)
        {
            throw new InvalidOperationException(
                $"Mission {missionId} held a resource slot with no id. The state machine cannot produce this.");
        }

        await vehicles.ReleaseFromMissionAsync(vehicle, missionId, cancellationToken).ConfigureAwait(false);
        await drivers.ReleaseFromMissionAsync(driver, missionId, cancellationToken).ConfigureAwait(false);
    }
}
