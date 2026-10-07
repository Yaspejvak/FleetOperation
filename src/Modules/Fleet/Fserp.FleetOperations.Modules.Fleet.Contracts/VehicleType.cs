namespace Fserp.FleetOperations.Modules.Fleet.Contracts;

/// <summary>
/// The closed list of vehicle types, defined once here (owner decision X-5). Drivers references this
/// project for this list only, so a qualification and a vehicle always name a type the same way.
/// </summary>
/// <remarks>
/// Values are explicit and never renumbered. Zero is deliberately not a member, so an unset value is
/// never a valid type. Persisted as the member name.
/// </remarks>
public enum VehicleType
{
    /// <summary>A van.</summary>
    Van = 1,

    /// <summary>A truck.</summary>
    Truck = 2,

    /// <summary>A heavy truck.</summary>
    HeavyTruck = 3,
}
