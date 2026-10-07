namespace Fserp.FleetOperations.Modules.Fleet.Domain;

/// <summary>
/// Whether the vehicle is in operation. Changed only by Change Status; independent of the maintenance
/// status (F-1). Values are explicit and never renumbered; zero is not a member. Persisted as the name.
/// </summary>
public enum OperationalStatus
{
    /// <summary>In operation. The status every vehicle is registered with (F-5).</summary>
    Active = 1,

    /// <summary>Taken out of operation.</summary>
    Inactive = 2,
}
