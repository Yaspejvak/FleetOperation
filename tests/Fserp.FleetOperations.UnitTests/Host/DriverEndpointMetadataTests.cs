using Fserp.FleetOperations.Api.Rest.Endpoints;
using Fserp.FleetOperations.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;

namespace Fserp.FleetOperations.UnitTests.Host;

/// <summary>
/// The REST surface of the Drivers module as the routing system sees it: routes, verbs and the policy on
/// each (D-3 and decision 5). The 401/403 behaviour with real tokens is in
/// <c>DriverEndpointAuthorizationTests</c>; this proves every endpoint carries the narrowest named policy
/// and none is anonymous.
/// </summary>
public sealed class DriverEndpointMetadataTests : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly IReadOnlyList<RouteEndpoint> _endpoints;

    public DriverEndpointMetadataTests()
    {
        var builder = WebApplication.CreateSlimBuilder();
        // Only so the route handler knows IMessageBus is a service; no request is sent in these tests.
        builder.Services.AddSingleton<IMessageBus>(static _ => throw new InvalidOperationException("Not used."));
        _app = builder.Build();
        _app.MapDriverEndpoints();
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
    [InlineData("POST", "/api/drivers/", FleetOperationsPolicies.FleetManager)]
    [InlineData("PUT", "/api/drivers/{driverId:guid}/status", FleetOperationsPolicies.FleetManager)]
    [InlineData("GET", "/api/drivers/{driverId:guid}", FleetOperationsPolicies.OperationalReader)]
    [InlineData("GET", "/api/drivers/available", FleetOperationsPolicies.OperationalReader)]
    public void Each_endpoint_carries_its_one_policy(string method, string pattern, string policy)
    {
        var endpoint = Single(method, pattern);

        var authorization = Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
        Assert.Equal(policy, authorization.Policy);
        Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
    }

    [Fact]
    public void The_module_maps_exactly_four_endpoints()
    {
        // The census of the Drivers REST surface: every mapped endpoint is one of the rows above, so a
        // fifth cannot appear without a policy row naming it. There is no gRPC service for drivers
        // (docs/plans/drivers.md says "gRPC: none" for both queries).
        Assert.Equal(4, _endpoints.Count);
    }

    [Fact]
    public void Every_route_hangs_below_the_modules_one_prefix()
    {
        // docs/architecture.md: "Keep one route prefix per module so the boundary survives at the edge."
        Assert.All(_endpoints, endpoint =>
            Assert.StartsWith("/api/drivers", endpoint.RoutePattern.RawText!, StringComparison.Ordinal));
    }

    [Fact]
    public void The_driver_id_route_is_constrained_to_a_guid_so_it_cannot_shadow_available()
    {
        // Both routes sit at the same depth under /api/drivers. Without the constraint, {driverId} would
        // also match the literal "available". The constraint is what keeps the two apart, and the real
        // matcher is exercised in DriverEndpointAuthorizationTests.
        var byId = Single("GET", "/api/drivers/{driverId:guid}");
        var parameter = Assert.Single(byId.RoutePattern.Parameters);

        Assert.Equal("driverId", parameter.Name);
        Assert.Equal("guid", Assert.Single(parameter.ParameterPolicies).Content);
        Assert.Empty(Single("GET", "/api/drivers/available").RoutePattern.Parameters);
    }
}
