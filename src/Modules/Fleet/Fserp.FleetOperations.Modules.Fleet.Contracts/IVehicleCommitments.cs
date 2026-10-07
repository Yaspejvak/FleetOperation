namespace Fserp.FleetOperations.Modules.Fleet.Contracts;

/// <summary>
/// Commits a vehicle to a mission and releases it. <b>This Contracts interface writes, by a recorded
/// decision</b> (docs/architecture.md, decision 1, accepted by the owner).
/// </summary>
/// <remarks>
/// <para>
/// Reason: assigning a mission and committing its vehicle must succeed or fail together in one
/// transaction, and the vehicle's own rules (operational, maintenance, capacity, availability) must be
/// checked by the <c>Vehicle</c> aggregate against the row being written, so that the vehicle's
/// concurrency token catches a concurrent assignment, status change or maintenance start. Fleet and
/// Operations share one <c>AppDbContext</c> in one deployment, and messaging is <c>none</c>.
/// </para>
/// <para>
/// Revisit when Fleet becomes a separate service or messaging is introduced: the commitment then becomes
/// a module message answered by Fleet in its own transaction.
/// </para>
/// <para>
/// The implementation loads the aggregate and calls its method; it does not save. The caller's unit of
/// work commits. A broken rule surfaces as <c>BusinessRuleValidationException</c> under the
/// <c>fleet</c> error domain.
/// </para>
/// </remarks>
public interface IVehicleCommitments
{
    /// <summary>Commits the vehicle to the mission, checking the vehicle's assignment rules.</summary>
    /// <param name="vehicleId">The vehicle's identity.</param>
    /// <param name="missionId">The mission being assigned.</param>
    /// <param name="requiredCapacityKg">The mission's required capacity in kilograms.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task CommitToMissionAsync(Guid vehicleId, Guid missionId, decimal requiredCapacityKg, CancellationToken cancellationToken);

    /// <summary>Releases the vehicle from the mission it is committed to.</summary>
    /// <param name="vehicleId">The vehicle's identity.</param>
    /// <param name="missionId">The mission that held the vehicle.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task ReleaseFromMissionAsync(Guid vehicleId, Guid missionId, CancellationToken cancellationToken);
}
