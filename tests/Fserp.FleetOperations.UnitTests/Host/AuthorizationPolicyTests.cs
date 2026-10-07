using Fserp.FleetOperations.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Extensions.Options;
using MPCore.Security.AspNetCore;

namespace Fserp.FleetOperations.UnitTests.Host;

public sealed class AuthorizationPolicyTests
{
    // Arbitrary test values: the real values are configuration and never appear in code.
    private static readonly AuthorizationRoleOptions Roles = new()
    {
        Operator = "test-operator",
        FleetManager = "test-fleet-manager",
        Administrator = "test-administrator",
    };

    private static AuthorizationOptions Contribute(AuthorizationRoleOptions roles)
    {
        var options = new AuthorizationOptions();
        new FleetOperationsPolicyContributor(Options.Create(roles)).Contribute(options);
        return options;
    }

    private static IReadOnlyList<string> RolesOf(AuthorizationPolicy policy) =>
        Assert.Single(policy.Requirements.OfType<MPCoreRoleRequirement>()).Roles;

    [Theory]
    [InlineData(FleetOperationsPolicies.Operator, new[] { "test-operator" })]
    [InlineData(FleetOperationsPolicies.FleetManager, new[] { "test-fleet-manager" })]
    [InlineData(FleetOperationsPolicies.Administrator, new[] { "test-administrator" })]
    [InlineData(FleetOperationsPolicies.OperationalReader, new[] { "test-operator", "test-fleet-manager" })]
    public void Each_policy_requires_an_authenticated_caller_with_its_configured_roles(string name, string[] roles)
    {
        var policy = Contribute(Roles).GetPolicy(name);

        Assert.NotNull(policy);
        Assert.Contains(policy.Requirements, requirement => requirement is DenyAnonymousAuthorizationRequirement);
        Assert.Equal(roles, RolesOf(policy));
    }

    [Fact]
    public void The_reader_accepts_either_role_through_one_requirement()
    {
        // Two role requirements would combine with AND; one requirement with two roles is OR.
        var policy = Contribute(Roles).GetPolicy(FleetOperationsPolicies.OperationalReader)!;

        Assert.Single(policy.Requirements.OfType<MPCoreRoleRequirement>());
    }

    [Fact]
    public void Administrator_is_not_a_superset_role()
    {
        // AD-1: the administrator reads the audit trail only.
        var options = Contribute(Roles);

        Assert.DoesNotContain("test-administrator", RolesOf(options.GetPolicy(FleetOperationsPolicies.FleetManager)!));
        Assert.DoesNotContain("test-administrator", RolesOf(options.GetPolicy(FleetOperationsPolicies.OperationalReader)!));
    }

    [Theory]
    [InlineData(null, "m", "a")]
    [InlineData("o", "", "a")]
    [InlineData("o", "m", "  ")]
    public void A_missing_role_value_refuses_to_build_the_policies(string? @operator, string? fleetManager, string? administrator)
    {
        var roles = new AuthorizationRoleOptions { Operator = @operator, FleetManager = fleetManager, Administrator = administrator };

        Assert.False(roles.IsComplete());
        Assert.Throws<InvalidOperationException>(() => Contribute(roles));
    }
}
