namespace Fserp.FleetOperations.Api.Security;

/// <summary>
/// The four authorization policies of this host, named in one place (docs/architecture.md, decision 5)
/// and applied identically to REST endpoints and gRPC services. The role each policy requires is read
/// from configuration (<see cref="AuthorizationRoleOptions"/>); no role, realm or client name is written
/// in code.
/// </summary>
public static class FleetOperationsPolicies
{
    /// <summary>Mission Create, Schedule, Assign, Start, Complete, Cancel. Requires the operator role.</summary>
    public const string Operator = "Operator";

    /// <summary>
    /// Vehicle Register and Change Status, maintenance Start and Complete, driver Register and Change
    /// Status. Requires the fleet manager role.
    /// </summary>
    public const string FleetManager = "FleetManager";

    /// <summary>The audit read surface. Requires the administrator role; not a superset role (AD-1).</summary>
    public const string Administrator = "Administrator";

    /// <summary>
    /// Every read (vehicles, drivers, missions). Requires the operator role or the fleet manager role
    /// (A-1). A policy of its own because several policies on one endpoint combine with AND.
    /// </summary>
    public const string OperationalReader = "OperationalReader";
}
