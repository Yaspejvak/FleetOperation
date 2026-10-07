namespace Fserp.FleetOperations.Modules.Administration.Application.Views;

/// <summary>
/// What kind of actor an entry recorded, as this module's REST contract names it. One member per member
/// of MP Core's <c>AuditActorKind</c>, with the same names, and owned here for the same reason as
/// <see cref="AuditEntryCategory"/>.
/// </summary>
public enum AuditEntryActorKind
{
    /// <summary>No authenticated identity; recorded for rejected attempts.</summary>
    Anonymous = 0,

    /// <summary>A human end user.</summary>
    User = 1,

    /// <summary>A machine client acting under its own credentials.</summary>
    Service = 2,

    /// <summary>The system itself: a background job, a migration, a scheduled process.</summary>
    System = 3,
}
