using Fserp.FleetOperations.Modules.Drivers.Application.Ports;
using Fserp.FleetOperations.Modules.Drivers.Contracts;

namespace Fserp.FleetOperations.Modules.Drivers.Application.Contracts;

/// <summary>
/// Drivers' implementation of <see cref="IDriverEligibilityReader"/>, the read-only snapshot another
/// module may take of one driver (docs/architecture.md, decision 1).
/// </summary>
/// <remarks>
/// <para>
/// It reads through the module's own read port, which goes to PostgreSQL. Nothing about drivers is
/// cached at all (D-6), and this module references no caching package, so the snapshot cannot be served
/// from a cache even by mistake.
/// </para>
/// <para>
/// It is a translation, not a decision: no rule is evaluated here. Every precondition the snapshot is read
/// for is checked again by <c>Driver</c> itself against the row the Assign transaction writes.
/// </para>
/// </remarks>
/// <param name="drivers">The Drivers read port.</param>
public sealed class DriverEligibilityReader(IDriverReadModel drivers) : IDriverEligibilityReader
{
    /// <inheritdoc />
    public Task<DriverSnapshot?> GetAsync(Guid driverId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(drivers);
        return drivers.GetSnapshotAsync(driverId, cancellationToken);
    }
}
