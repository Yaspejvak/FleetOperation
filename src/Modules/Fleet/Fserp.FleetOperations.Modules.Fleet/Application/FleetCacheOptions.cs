namespace Fserp.FleetOperations.Modules.Fleet.Application;

/// <summary>
/// The Fleet module's cache settings (docs/architecture.md, decision 4). Registered with its defaults by
/// <c>AddFleetModule</c>; no configuration key is required and no connection string is ever named here.
/// </summary>
public sealed class FleetCacheOptions
{
    /// <summary>
    /// How long the available-vehicles entry may live: 30 seconds absolute (F-6). It is the bound on
    /// staleness when an eviction is missed or when a second instance's in-process level still holds the
    /// old list, so it is never unset.
    /// </summary>
    public TimeSpan AvailableVehiclesExpiration { get; set; } = TimeSpan.FromSeconds(30);
}
