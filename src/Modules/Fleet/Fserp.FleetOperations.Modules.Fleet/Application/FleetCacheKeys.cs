namespace Fserp.FleetOperations.Modules.Fleet.Application;

/// <summary>
/// The cache keys of the Fleet module, each declared exactly once (docs/architecture.md, decision 4).
/// </summary>
public static class FleetCacheKeys
{
    /// <summary>
    /// The one entry holding the whole available-vehicles list. Read by the query handler and removed by
    /// the eviction handler: one constant, two call sites.
    /// </summary>
    /// <remarks>
    /// The key is passed bare. <c>MPCoreCacheOptions.KeyPrefix</c> is applied by the adapter itself
    /// (<c>HybridCacheAdapter.Qualify</c>), so prepending it here would qualify the key twice and the
    /// eviction would miss the entry the read created. <c>v1</c> is the shape version of
    /// <see cref="Views.AvailableVehicleView"/>; bump it when that record changes.
    /// </remarks>
    public const string AvailableVehicles = "fleet:vehicles:available:v1";
}
