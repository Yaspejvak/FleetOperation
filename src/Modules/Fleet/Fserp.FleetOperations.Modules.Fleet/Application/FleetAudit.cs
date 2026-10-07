using Fserp.FleetOperations.Modules.Fleet.Domain;

namespace Fserp.FleetOperations.Modules.Fleet.Application;

/// <summary>
/// The names the Fleet module records business actions under (docs/plans/fleet.md, "Business audit").
/// The actor is never passed: the recorder takes it from the validated token.
/// </summary>
public static class FleetAudit
{
    /// <summary>The audit module name.</summary>
    public const string Module = "Fleet";

    /// <summary>The audited entity type.</summary>
    public const string VehicleEntity = nameof(Vehicle);

    /// <summary>A vehicle was registered (recommended action).</summary>
    public const string VehicleRegistered = nameof(VehicleRegistered);

    /// <summary>A vehicle's operational status changed (required action); also the action of its rejected attempt.</summary>
    public const string VehicleStatusChanged = nameof(VehicleStatusChanged);

    /// <summary>A vehicle entered maintenance (required action); also the action of its rejected attempt.</summary>
    public const string MaintenanceStarted = nameof(MaintenanceStarted);

    /// <summary>
    /// A vehicle left maintenance (required action). fleet.md requires no rejected attempt for Complete
    /// Maintenance, so none is recorded.
    /// </summary>
    public const string MaintenanceCompleted = nameof(MaintenanceCompleted);

    /// <summary>Metadata key: the status before the change.</summary>
    public const string FromMetadata = "from";

    /// <summary>Metadata key: the requested status.</summary>
    public const string ToMetadata = "to";
}
