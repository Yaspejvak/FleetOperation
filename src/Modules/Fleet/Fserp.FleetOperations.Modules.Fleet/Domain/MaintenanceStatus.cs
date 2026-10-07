namespace Fserp.FleetOperations.Modules.Fleet.Domain;

/// <summary>
/// Whether the vehicle is under maintenance. Changed only by Start and Complete Maintenance; independent
/// of the operational status (F-1). Values are explicit and never renumbered; zero is not a member.
/// Persisted as the name.
/// </summary>
public enum MaintenanceStatus
{
    /// <summary>Not under maintenance. The status every vehicle is registered with.</summary>
    NotUnderMaintenance = 1,

    /// <summary>Under maintenance.</summary>
    UnderMaintenance = 2,
}
