namespace Fserp.FleetOperations.Modules.Administration.Application.Views;

/// <summary>
/// What kind of fact an entry records, as this module's REST contract names it. One member per member of
/// MP Core's <c>AuditCategory</c>, with the same names, and owned here on purpose: the package type is
/// never exposed, so the contract does not move if the package's enum does.
/// </summary>
public enum AuditEntryCategory
{
    /// <summary>A persisted entity was created, updated or deleted.</summary>
    EntityChange = 0,

    /// <summary>A business action was attempted, with its outcome.</summary>
    BusinessAction = 1,
}
