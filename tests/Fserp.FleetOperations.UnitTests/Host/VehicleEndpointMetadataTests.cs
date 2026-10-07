using Fserp.FleetOperations.Api.Rest.Endpoints;
using Fserp.FleetOperations.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;

namespace Fserp.FleetOperations.UnitTests.Host;

/// <summary>
/// The REST surface of the slice as the routing system sees it: routes, verbs and the policy on each.
/// The 401/403 behaviour with real tokens is an integration concern; this proves every endpoint carries
/// the narrowest named policy and none is anonymous.
/// </summary>
public sealed class VehicleEndpointMetadataTests : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly IReadOnlyList<RouteEndpoint> _endpoints;

    public VehicleEndpointMetadataTests()
    {
        var builder = WebApplication.CreateSlimBuilder();
        // Only so the route handler knows IMessageBus is a service; no request is sent in these tests.
        builder.Services.AddSingleton<IMessageBus>(static _ => throw new InvalidOperationException("Not used."));
        _app = builder.Build();
        _app.MapVehicleEndpoints();
        _endpoints = ((IEndpointRouteBuilder)_app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToList();
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    private RouteEndpoint Single(string method, string pattern) =>
        Assert.Single(_endpoints, endpoint =>
            endpoint.RoutePattern.RawText == pattern
            && endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()!.HttpMethods.Contains(method));

    [Theory]
    [InlineData("POST", "/api/fleet/vehicles", FleetOperationsPolicies.FleetManager)]
    [InlineData("PUT", "/api/fleet/vehicles/{vehicleId:guid}/status", FleetOperationsPolicies.FleetManager)]
    [InlineData("POST", "/api/fleet/vehicles/{vehicleId:guid}/maintenance/start", FleetOperationsPolicies.FleetManager)]
    [InlineData("POST", "/api/fleet/vehicles/{vehicleId:guid}/maintenance/complete", FleetOperationsPolicies.FleetManager)]
    [InlineData("GET", "/api/fleet/vehicles/{vehicleId:guid}", FleetOperationsPolicies.OperationalReader)]
    [InlineData("GET", "/api/fleet/vehicles/available", FleetOperationsPolicies.OperationalReader)]
    public void Each_endpoint_carries_its_one_policy(string method, string pattern, string policy)
    {
        var endpoint = Single(method, pattern);

        var authorization = Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
        Assert.Equal(policy, authorization.Policy);
        Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
    }

    [Fact]
    public void The_module_maps_exactly_six_endpoints()
    {
        // The census of the Fleet REST surface after rounds 1 to 3 (L-6): every mapped endpoint is one of
        // the rows above, so a seventh endpoint cannot appear without a policy row naming it.
        Assert.Equal(6, _endpoints.Count);
    }
}
