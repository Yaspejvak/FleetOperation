namespace Fserp.FleetOperations.Modules.Drivers.Domain;

/// <summary>
/// Whether the driver is in service. Changed only by Change Driver Status, which accepts these two values
/// and nothing else (D-2). Values are explicit and never renumbered; zero is not a member. Persisted as
/// the name.
/// </summary>
/// <remarks>
/// Drivers' own enum, not Fleet's. Only <c>VehicleType</c> crosses from <c>Fleet.Contracts</c> (X-5); a
/// driver's status and a vehicle's status happen to share two names and are not the same vocabulary.
/// </remarks>
public enum OperationalStatus
{
    /// <summary>In service. The status every driver is registered with.</summary>
    Active = 1,

    /// <summary>Taken out of service.</summary>
    Inactive = 2,
}
