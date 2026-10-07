using Fserp.FleetOperations.Modules.Fleet.Contracts;

namespace Fserp.FleetOperations.Modules.Drivers.Contracts;

/// <summary>
/// Commits a driver to a mission and releases it. <b>This Contracts interface writes, by a recorded
/// decision</b> (docs/architecture.md, decision 1, accepted by the owner).
/// </summary>
/// <remarks>
/// <para>
/// Reason: assigning a mission and committing its driver must succeed or fail together in one
/// transaction, and the driver's own rules (active, available, qualified for the vehicle type) must be
/// checked by the <c>Driver</c> aggregate against the row being written, so that the driver's
/// concurrency token catches a concurrent assignment or deactivation. Drivers and Operations share one
/// <c>AppDbContext</c> in one deployment, and messaging is <c>none</c>.
/// </para>
/// <para>
/// Revisit when Drivers becomes a separate service or messaging is introduced.
/// </para>
/// <para>
/// The implementation loads the aggregate and calls its method; it does not save. The caller's unit of
/// work commits. A broken rule surfaces as <c>BusinessRuleValidationException</c> under the
/// <c>drivers</c> error domain.
/// </para>
/// </remarks>
public interface IDriverCommitments
{
    /// <summary>Commits the driver to the mission, checking the driver's assignment rules.</summary>
    /// <param name="driverId">The driver's identity.</param>
    /// <param name="missionId">The mission being assigned.</param>
    /// <param name="vehicleType">The type of the vehicle assigned to the same mission.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task CommitToMissionAsync(Guid driverId, Guid missionId, VehicleType vehicleType, CancellationToken cancellationToken);

    /// <summary>Releases the driver from the mission it is committed to.</summary>
    /// <param name="driverId">The driver's identity.</param>
    /// <param name="missionId">The mission that held the driver.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task ReleaseFromMissionAsync(Guid driverId, Guid missionId, CancellationToken cancellationToken);
}
