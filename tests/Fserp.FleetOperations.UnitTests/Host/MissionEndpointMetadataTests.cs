using Fserp.FleetOperations.Api.Rest.Endpoints;
using Fserp.FleetOperations.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;

namespace Fserp.FleetOperations.UnitTests.Host;

/// <summary>
/// The REST surface of the Operations module as the routing system sees it: routes, verbs and the policy
/// on each (docs/plans/operations.md, "Authorization", and decision 5). The 401/403 behaviour with real
/// tokens is in <c>MissionEndpointAuthorizationTests</c>; this proves every endpoint carries the narrowest
/// named policy and none is anonymous.
/// </summary>
public sealed class MissionEndpointMetadataTests : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly IReadOnlyList<RouteEndpoint> _endpoints;

    public MissionEndpointMetadataTests()
    {
        var builder = WebApplication.CreateSlimBuilder();
        // Only so the route handler knows IMessageBus is a service; no request is sent in these tests.
        builder.Services.AddSingleton<IMessageBus>(static _ => throw new InvalidOperationException("Not used."));
        _app = builder.Build();
        _app.MapMissionEndpoints();
        _endpoints = ((IEndpointRouteBuilder)_app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToList();
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    private RouteEndpoint Single(string method, string pattern) =>
        Assert.Single(_endpoints, endpoint =>
            endpoint.RoutePattern.RawText == pattern
            && endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains(method));

    [Theory]
    [InlineData("POST", "/api/operations/missions", FleetOperationsPolicies.Operator)]
    [InlineData("POST", "/api/operations/missions/{missionId:guid}/schedule", FleetOperationsPolicies.Operator)]
    [InlineData("POST", "/api/operations/missions/{missionId:guid}/assign", FleetOperationsPolicies.Operator)]
    [InlineData("POST", "/api/operations/missions/{missionId:guid}/start", FleetOperationsPolicies.Operator)]
    [InlineData("POST", "/api/operations/missions/{missionId:guid}/complete", FleetOperationsPolicies.Operator)]
    [InlineData("POST", "/api/operations/missions/{missionId:guid}/cancel", FleetOperationsPolicies.Operator)]
    [InlineData("GET", "/api/operations/missions/{missionId:guid}", FleetOperationsPolicies.OperationalReader)]
    [InlineData("GET", "/api/operations/missions/active", FleetOperationsPolicies.OperationalReader)]
    public void Each_endpoint_carries_its_one_policy(string method, string pattern, string policy)
    {
        var endpoint = Single(method, pattern);

        var authorization = Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
        Assert.Equal(policy, authorization.Policy);
        Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
    }

    [Fact]
    public void The_module_maps_exactly_eight_endpoints()
    {
        // The census of the Operations REST surface: every mapped endpoint is one of the rows above, so a
        // ninth cannot appear without a policy row naming it. The six commands are the plan's command
        // table and the two reads are its query table; nothing else is mapped.
        Assert.Equal(8, _endpoints.Count);
    }

    [Fact]
    public void Every_command_is_a_POST_and_every_read_is_a_GET()
    {
        var methods = _endpoints
            .Select(endpoint => (
                endpoint.RoutePattern.RawText!,
                Method: endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Single()))
            .ToList();

        Assert.Equal(6, methods.Count(entry => entry.Method == "POST"));
        Assert.Equal(2, methods.Count(entry => entry.Method == "GET"));
    }

    [Fact]
    public void Every_route_hangs_below_the_modules_one_prefix()
    {
        // docs/architecture.md: "Keep one route prefix per module so the boundary survives at the edge."
        Assert.All(_endpoints, endpoint =>
            Assert.StartsWith("/api/operations", endpoint.RoutePattern.RawText!, StringComparison.Ordinal));
    }

    [Fact]
    public void The_mission_id_route_is_constrained_to_a_guid_so_it_cannot_shadow_active()
    {
        // Both routes sit at the same depth under /api/operations/missions. Without the constraint,
        // {missionId} would also match the literal "active". The constraint is what keeps the two apart,
        // and the real matcher is exercised in MissionEndpointContractTests.
        var byId = Single("GET", "/api/operations/missions/{missionId:guid}");
        var parameter = Assert.Single(byId.RoutePattern.Parameters);

        Assert.Equal("missionId", parameter.Name);
        Assert.Equal("guid", Assert.Single(parameter.ParameterPolicies).Content);
        Assert.Empty(Single("GET", "/api/operations/missions/active").RoutePattern.Parameters);
    }

    [Fact]
    public void Every_transition_route_constrains_its_mission_id_too()
    {
        // The same constraint on the five sub-routes, so /missions/anything/start cannot reach a handler
        // with a parse failure instead of a 404.
        foreach (var transition in new[] { "schedule", "assign", "start", "complete", "cancel" })
        {
            var endpoint = Single("POST", $"/api/operations/missions/{{missionId:guid}}/{transition}");
            var parameter = Assert.Single(endpoint.RoutePattern.Parameters);

            Assert.Equal("guid", Assert.Single(parameter.ParameterPolicies).Content);
        }
    }

    [Fact]
    public void The_create_route_is_named_so_the_201_Location_header_can_point_at_the_read()
    {
        Assert.Equal("GetMission", MissionEndpoints.GetMissionRoute);
        Assert.Contains(
            _endpoints,
            endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == MissionEndpoints.GetMissionRoute);
    }
}
