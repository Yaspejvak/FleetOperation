using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using MPCore.Security.AspNetCore;

namespace Fserp.FleetOperations.Api.Security;

/// <summary>
/// Adds the four named policies to ASP.NET Core authorization, each requiring a role through
/// <c>RequireMPCoreRole</c>, which reads the roles of the validated <c>CurrentActor</c> only (never a
/// top-level <c>role</c> claim, never a header). The authenticated fallback policy stays in place as
/// the safety net for anything without a policy.
/// </summary>
/// <param name="roles">The role values from configuration.</param>
public sealed class FleetOperationsPolicyContributor(IOptions<AuthorizationRoleOptions> roles)
    : IMPCoreAuthorizationPolicyContributor
{
    /// <inheritdoc />
    public void Contribute(AuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var configured = roles.Value;
        if (!configured.IsComplete())
        {
            throw new InvalidOperationException(
                $"Every role value under {AuthorizationRoleOptions.Section} (Operator, FleetManager, Administrator) is required.");
        }

        options.AddPolicy(FleetOperationsPolicies.Operator, policy => policy
            .RequireAuthenticatedUser()
            .RequireMPCoreRole(configured.Operator!));
        options.AddPolicy(FleetOperationsPolicies.FleetManager, policy => policy
            .RequireAuthenticatedUser()
            .RequireMPCoreRole(configured.FleetManager!));
        options.AddPolicy(FleetOperationsPolicies.Administrator, policy => policy
            .RequireAuthenticatedUser()
            .RequireMPCoreRole(configured.Administrator!));

        // One requirement with two accepted roles: satisfied by either (OR), unlike two policies (AND).
        options.AddPolicy(FleetOperationsPolicies.OperationalReader, policy => policy
            .RequireAuthenticatedUser()
            .RequireMPCoreRole(configured.Operator!, configured.FleetManager!));
    }
}
