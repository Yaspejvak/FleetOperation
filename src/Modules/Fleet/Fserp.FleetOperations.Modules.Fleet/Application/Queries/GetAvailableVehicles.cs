using Fserp.FleetOperations.Modules.Fleet.Application.Ports;
using Fserp.FleetOperations.Modules.Fleet.Application.Views;
using Microsoft.Extensions.Options;
using MPCore.Application.Messaging;
using MPCore.Caching.Abstractions;

namespace Fserp.FleetOperations.Modules.Fleet.Application.Queries;

/// <summary>
/// Every available vehicle. No parameters: there is no filter and no paging (F-7), which is also what
/// makes a single cache entry enough.
/// </summary>
public sealed record GetAvailableVehicles : IQuery<IReadOnlyList<AvailableVehicleView>>;

/// <summary>
/// Handles <see cref="GetAvailableVehicles"/>. Reads only: no unit of work, no repository, nothing
/// published. The list is served through the hybrid cache (decision 4).
/// </summary>
public static class GetAvailableVehiclesHandler
{
    /// <summary>
    /// Returns the cached list, or reads it from the database and caches it for
    /// <see cref="FleetCacheOptions.AvailableVehiclesExpiration"/>.
    /// </summary>
    /// <param name="query">The query.</param>
    /// <param name="cache">The read-through cache; the hybrid adapter runs one factory per key per instance under concurrent misses.</param>
    /// <param name="vehicles">The vehicle read model, used only when the entry is absent.</param>
    /// <param name="options">The module's cache settings.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <remarks>
    /// The cached value is an array, not an interface: it has to survive the adapter's serializer in both
    /// directions, and an array is the concrete shape that does so without depending on how the serializer
    /// materializes a collection interface. The array is returned as its
    /// <see cref="IReadOnlyList{T}"/>, which costs nothing and is what the contract promises.
    /// </remarks>
    public static async Task<IReadOnlyList<AvailableVehicleView>> Handle(
        GetAvailableVehicles query,
        IReadThroughCache cache,
        IVehicleReadModel vehicles,
        IOptions<FleetCacheOptions> options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(vehicles);
        ArgumentNullException.ThrowIfNull(options);

        // The key is passed bare: the adapter applies MPCoreCacheOptions.KeyPrefix itself.
        return await cache.GetOrCreateAsync(
                FleetCacheKeys.AvailableVehicles,
                async token => (await vehicles.GetAvailableAsync(token).ConfigureAwait(false)).ToArray(),
                options.Value.AvailableVehiclesExpiration,
                cancellationToken)
            .ConfigureAwait(false);
    }
}
