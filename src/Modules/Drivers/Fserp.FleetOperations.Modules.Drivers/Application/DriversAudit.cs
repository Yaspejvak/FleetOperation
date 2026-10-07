using Fserp.FleetOperations.Modules.Drivers.Domain;

namespace Fserp.FleetOperations.Modules.Drivers.Application;

/// <summary>
/// The names the Drivers module records business actions under (docs/plans/drivers.md, "Business audit").
/// The actor is never passed: the recorder takes it from the validated token.
/// </summary>
public static class DriversAudit
{
    /// <summary>The audit module name.</summary>
    public const string Module = "Drivers";

    /// <summary>The audited entity type.</summary>
    public const string DriverEntity = nameof(Driver);

    /// <summary>A driver was registered (recommended action).</summary>
    public const string DriverRegistered = nameof(DriverRegistered);

    /// <summary>
    /// A driver's operational status changed (recommended action); also the action of its rejected attempt.
    /// </summary>
    public const string DriverStatusChanged = nameof(DriverStatusChanged);

    /// <summary>Metadata key: the status before the change.</summary>
    public const string FromMetadata = "from";

    /// <summary>Metadata key: the requested status.</summary>
    public const string ToMetadata = "to";
}
