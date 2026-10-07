namespace Fserp.FleetOperations.Api.Security;

/// <summary>
/// The role values the policies require, bound from <c>Authorization:Roles</c>. They are the normalized
/// role names MP Core extracts from the token through <c>Security:ClaimMapping</c> (its role sources),
/// so they depend on the identity provider and are configuration, never code. The host refuses to start
/// when any of them is missing.
/// </summary>
public sealed class AuthorizationRoleOptions
{
    /// <summary>The configuration section.</summary>
    public const string Section = "Authorization:Roles";

    /// <summary>The operator role value.</summary>
    public string? Operator { get; set; }

    /// <summary>The fleet manager role value.</summary>
    public string? FleetManager { get; set; }

    /// <summary>The administrator role value.</summary>
    public string? Administrator { get; set; }

    /// <summary>True when every role value is present.</summary>
    public bool IsComplete() =>
        !string.IsNullOrWhiteSpace(Operator)
        && !string.IsNullOrWhiteSpace(FleetManager)
        && !string.IsNullOrWhiteSpace(Administrator);
}
