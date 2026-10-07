namespace Fserp.FleetOperations.Modules.Administration.Application.Views;

/// <summary>
/// The outcome of an audited action, as this module's REST contract names it. One member per member of
/// MP Core's <c>AuditOutcome</c>, with the same names, and owned here for the same reason as
/// <see cref="AuditEntryCategory"/>.
/// </summary>
public enum AuditEntryOutcome
{
    /// <summary>The action completed and its effects were committed.</summary>
    Succeeded = 0,

    /// <summary>Refused by a rule or by authorization; nothing was changed.</summary>
    Rejected = 1,

    /// <summary>Attempted and failed for a reason other than a rule; nothing was committed.</summary>
    Failed = 2,
}
