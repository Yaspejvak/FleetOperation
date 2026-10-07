using Fserp.FleetOperations.Modules.Fleet.Domain.Events;
using Microsoft.Extensions.Logging;
using MPCore.Caching.Abstractions;

namespace Fserp.FleetOperations.Modules.Fleet.Application.Events;

/// <summary>
/// Removes the cached available-vehicles list whenever a vehicle's availability may have changed
/// (docs/architecture.md, decision 4). One <c>Handle</c> per domain event in
/// docs/plans/fleet.md, "Domain events"; the domain events are the only place every availability change
/// already passes.
/// </summary>
/// <remarks>
/// <para>
/// Removal is idempotent and the key is the same for every event, so at-least-once delivery is harmless:
/// a second delivery removes an entry that is already gone.
/// </para>
/// <para>
/// Nothing is recomputed here. Writing the new list from the event would duplicate the query's logic and
/// could store a list the transaction had not committed; the next reader repopulates the entry.
/// </para>
/// <para>
/// <c>VehicleCommittedToMission</c> and <c>VehicleReleasedFromMission</c> are handled although no code
/// raises them yet: the commitment methods belong to a later round, and the handler is written once for
/// the whole list the plan names rather than being revisited then.
/// </para>
/// <para>
/// <strong>A failing removal is logged, not rethrown</strong> (docs/architecture.md, decision 4, as
/// amended). <c>DefaultHybridCache.RemoveAsync</c> does not catch a level-2 failure, so a Redis outage
/// surfaces here as an exception. By then the in-process level has already been cleared, and the Redis
/// entry dies by its 30 s TTL regardless. The outage therefore degrades <em>staleness</em>, which
/// decision 4 already bounds and accepts; it does not break event handling, and it must not turn an
/// eviction into a retry or a dead-letter. A cancelled <see cref="CancellationToken"/> is the one
/// failure that still propagates: that is a shutdown or an abandoned request, not a cache outage.
/// </para>
/// </remarks>
public static class AvailableVehiclesCacheEvictionHandler
{
    /// <summary>A new vehicle is registered Active and uncommitted, so it joins the list.</summary>
    /// <param name="domainEvent">The event.</param>
    /// <param name="cache">The cache.</param>
    /// <param name="logger">Records a removal that failed.</param>
    /// <param name="cancellationToken">Cancels the removal.</param>
    public static Task Handle(VehicleRegistered domainEvent, ICache cache, ILogger logger, CancellationToken cancellationToken) =>
        Evict(cache, logger, cancellationToken);

    /// <summary>Active joins the list and Inactive leaves it.</summary>
    /// <param name="domainEvent">The event.</param>
    /// <param name="cache">The cache.</param>
    /// <param name="logger">Records a removal that failed.</param>
    /// <param name="cancellationToken">Cancels the removal.</param>
    public static Task Handle(VehicleStatusChanged domainEvent, ICache cache, ILogger logger, CancellationToken cancellationToken) =>
        Evict(cache, logger, cancellationToken);

    /// <summary>A vehicle under maintenance leaves the list.</summary>
    /// <param name="domainEvent">The event.</param>
    /// <param name="cache">The cache.</param>
    /// <param name="logger">Records a removal that failed.</param>
    /// <param name="cancellationToken">Cancels the removal.</param>
    public static Task Handle(MaintenanceStarted domainEvent, ICache cache, ILogger logger, CancellationToken cancellationToken) =>
        Evict(cache, logger, cancellationToken);

    /// <summary>A vehicle out of maintenance may rejoin the list.</summary>
    /// <param name="domainEvent">The event.</param>
    /// <param name="cache">The cache.</param>
    /// <param name="logger">Records a removal that failed.</param>
    /// <param name="cancellationToken">Cancels the removal.</param>
    public static Task Handle(MaintenanceCompleted domainEvent, ICache cache, ILogger logger, CancellationToken cancellationToken) =>
        Evict(cache, logger, cancellationToken);

    /// <summary>A committed vehicle leaves the list.</summary>
    /// <param name="domainEvent">The event.</param>
    /// <param name="cache">The cache.</param>
    /// <param name="logger">Records a removal that failed.</param>
    /// <param name="cancellationToken">Cancels the removal.</param>
    public static Task Handle(VehicleCommittedToMission domainEvent, ICache cache, ILogger logger, CancellationToken cancellationToken) =>
        Evict(cache, logger, cancellationToken);

    /// <summary>A released vehicle may rejoin the list.</summary>
    /// <param name="domainEvent">The event.</param>
    /// <param name="cache">The cache.</param>
    /// <param name="logger">Records a removal that failed.</param>
    /// <param name="cancellationToken">Cancels the removal.</param>
    public static Task Handle(VehicleReleasedFromMission domainEvent, ICache cache, ILogger logger, CancellationToken cancellationToken) =>
        Evict(cache, logger, cancellationToken);

    private static async Task Evict(ICache cache, ILogger logger, CancellationToken cancellationToken)
    {
        // Outside the try on purpose: a missing port is a composition bug, not a cache outage, and must
        // still surface. So must anything else this method might ever do; only the call below is guarded.
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            // The same bare key the query handler reads; the adapter qualifies it with the key prefix.
            await cache.RemoveAsync(FleetCacheKeys.AvailableVehicles, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsCallerCancellation(exception, cancellationToken))
        {
            // Every exception type except the caller's own cancellation. The failure arrives from the
            // level-2 client through two layers this module must not reference (the hybrid adapter and
            // StackExchange.Redis, which the architecture tests forbid in Application/), so its type is
            // not knowable here; narrowing to a guess would let the real outage escape and fail the
            // handler, which is the defect being fixed. The guard is the try's width instead: it wraps
            // the one call, so a bug anywhere else in this handler still surfaces.
            logger.LogWarning(
                exception,
                "Evicting the available vehicles cache entry {CacheKey} failed. The in-process level is already cleared and any level-2 entry expires by its TTL, so the list is stale for at most its expiration.",
                FleetCacheKeys.AvailableVehicles);
        }
    }

    /// <summary>
    /// Cancellation asked for by this handler's caller: a shutdown or an abandoned request, which
    /// propagates. A cancellation the caller did not ask for — a level-2 client's own internal timeout,
    /// which surfaces as <see cref="TaskCanceledException"/> — is an outage and is logged like any other.
    /// </summary>
    private static bool IsCallerCancellation(Exception exception, CancellationToken cancellationToken) =>
        exception is OperationCanceledException && cancellationToken.IsCancellationRequested;
}
