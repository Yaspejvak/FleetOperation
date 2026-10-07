namespace Fserp.FleetOperations.Modules.Fleet.Domain;

/// <summary>
/// The status shown to a reader, derived from the two status fields: <see cref="UnderMaintenance"/> wins,
/// otherwise the operational status (docs/plans/fleet.md). Never persisted.
/// </summary>
public enum VehicleDisplayStatus
{
    /// <summary>Active and not under maintenance.</summary>
    Active = 1,

    /// <summary>Inactive and not under maintenance.</summary>
    Inactive = 2,

    /// <summary>Under maintenance, whatever the operational status.</summary>
    UnderMaintenance = 3,
}
