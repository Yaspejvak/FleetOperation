using Fserp.FleetOperations.Modules.Drivers.Application.Ports;
using Fserp.FleetOperations.Modules.Drivers.Application.Views;
using MPCore.Application.Messaging;

namespace Fserp.FleetOperations.Modules.Drivers.Application.Queries;

/// <summary>
/// Every available driver. No parameters: Get Available Drivers has no vehicle-type filter (D-6).
/// </summary>
public sealed record GetAvailableDrivers : IQuery<IReadOnlyList<AvailableDriverView>>;

/// <summary>
/// Handles <see cref="GetAvailableDrivers"/>. Reads only: no unit of work, no repository, nothing
/// published — and no cache.
/// </summary>
public static class GetAvailableDriversHandler
{
    /// <summary>Returns the available drivers, read from the database on every call.</summary>
    /// <param name="query">The query.</param>
    /// <param name="drivers">The driver read model.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <remarks>
    /// Unlike Fleet's available list, this one is not cached: D-6 and the plan's query table say "Cached:
    /// no" for both Drivers queries, so the handler declares no cache port at all. It returns the list
    /// itself rather than a <c>Result&lt;T&gt;</c>, as the plan's query table specifies and as Fleet's
    /// available list does (L-14): an empty fleet of drivers is <c>200 []</c>, never <c>404</c>.
    /// </remarks>
    public static async Task<IReadOnlyList<AvailableDriverView>> Handle(
        GetAvailableDrivers query,
        IDriverReadModel drivers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(drivers);

        return await drivers.GetAvailableAsync(cancellationToken).ConfigureAwait(false);
    }
}
