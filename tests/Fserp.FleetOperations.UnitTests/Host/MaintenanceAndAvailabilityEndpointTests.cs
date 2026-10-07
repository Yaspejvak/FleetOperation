using System.Net;
using System.Text.Json;
using Fserp.FleetOperations.Modules.Fleet.Application;
using Fserp.FleetOperations.Modules.Fleet.Application.Commands;
using Fserp.FleetOperations.Modules.Fleet.Application.Queries;
using Fserp.FleetOperations.Modules.Fleet.Application.Views;
using Fserp.FleetOperations.Modules.Fleet.Contracts;
using Fserp.FleetOperations.Modules.Fleet.Domain;
using Fserp.FleetOperations.Modules.Fleet.Domain.Rules;
using MPCore.Application.Results;
using MPCore.Domain.Rules;
using static Fserp.FleetOperations.UnitTests.Host.FleetOperationsHostFactory;

namespace Fserp.FleetOperations.UnitTests.Host;

/// <summary>
/// The endpoints added in rounds 2 and 3 through the real host pipeline with real signed bearer tokens
/// (docs/architecture.md, decision 5): the maintenance commands behind <c>FleetManager</c> and the
/// available list behind <c>OperationalReader</c>. The round 1 matrix in
/// <see cref="VehicleEndpointAuthorizationTests"/> is left exactly as it is.
/// </summary>
public sealed class MaintenanceAndAvailabilityAuthorizationTests : IClassFixture<FleetOperationsHostFactory>
{
    private readonly FleetOperationsHostFactory _host;

    public MaintenanceAndAvailabilityAuthorizationTests(FleetOperationsHostFactory host)
    {
        _host = host;
        _host.Bus.Reset(static message => message switch
        {
            GetAvailableVehicles => (object)(IReadOnlyList<AvailableVehicleView>)[],
            StartMaintenance command => Result<VehicleView>.Success(VehicleHttp.SampleView(command.VehicleId)),
            CompleteMaintenance command => Result<VehicleView>.Success(VehicleHttp.SampleView(command.VehicleId)),
            _ => Result<VehicleView>.Success(VehicleHttp.SampleView(VehicleHttp.VehicleId)),
        });
    }

    private static HttpRequestMessage Request(string endpoint, Guid? id = null) => endpoint switch
    {
        "start" => new HttpRequestMessage(HttpMethod.Post, $"/api/fleet/vehicles/{id ?? VehicleHttp.VehicleId}/maintenance/start"),
        "complete" => new HttpRequestMessage(HttpMethod.Post, $"/api/fleet/vehicles/{id ?? VehicleHttp.VehicleId}/maintenance/complete"),
        "available" => new HttpRequestMessage(HttpMethod.Get, "/api/fleet/vehicles/available"),
        _ => throw new ArgumentOutOfRangeException(nameof(endpoint)),
    };

    public static TheoryData<string> Endpoints() => ["start", "complete", "available"];

    private async Task AssertRefused(string endpoint, string? token, HttpStatusCode expected, string expectedCode)
    {
        var response = await _host.Client(token).SendAsync(Request(endpoint));

        await VehicleHttp.AssertProblem(response, expected, "mpcore.security", expectedCode);
        Assert.Empty(_host.Bus.Sent);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public Task No_token_is_401_and_never_reaches_the_bus(string endpoint) =>
        AssertRefused(endpoint, null, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");

    [Theory]
    [MemberData(nameof(Endpoints))]
    public Task A_valid_token_without_any_role_is_403(string endpoint) =>
        AssertRefused(endpoint, _host.Token(), HttpStatusCode.Forbidden, "FORBIDDEN");

    [Theory]
    [InlineData("start", OperatorRole)]
    [InlineData("start", AdministratorRole)]
    [InlineData("complete", OperatorRole)]
    [InlineData("complete", AdministratorRole)]
    [InlineData("available", AdministratorRole)]
    public Task A_wrong_role_is_403_and_never_reaches_the_bus(string endpoint, string role) =>
        AssertRefused(endpoint, _host.Token(role), HttpStatusCode.Forbidden, "FORBIDDEN");

    [Theory]
    [InlineData("start", FleetManagerRole)]
    [InlineData("complete", FleetManagerRole)]
    [InlineData("available", OperatorRole)]
    [InlineData("available", FleetManagerRole)]
    public async Task The_right_role_reaches_the_command_or_query(string endpoint, string role)
    {
        var response = await _host.Client(_host.Token(role)).SendAsync(Request(endpoint));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = Assert.Single(_host.Bus.Sent);
        Assert.IsType(
            endpoint switch
            {
                "start" => typeof(StartMaintenance),
                "complete" => typeof(CompleteMaintenance),
                _ => typeof(GetAvailableVehicles),
            },
            sent);
    }

    [Fact]
    public async Task An_operator_cannot_start_maintenance_even_with_identity_headers()
    {
        // CLAUDE.md: identity never comes from an arbitrary header, body, query or route value.
        var request = Request("start");
        request.Headers.Add("X-Forwarded-User", "someone-else");
        request.Headers.Add("X-Forwarded-Roles", FleetManagerRole);

        var response = await _host.Client(_host.Token(OperatorRole)).SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_host.Bus.Sent);
    }
}

/// <summary>
/// The REST contract of rounds 2 and 3: what each endpoint sends, how each outcome is mapped, and the
/// routing fact that <c>/vehicles/available</c> is not swallowed by <c>/vehicles/{vehicleId:guid}</c>.
/// </summary>
public sealed class MaintenanceAndAvailabilityContractTests : IClassFixture<FleetOperationsHostFactory>
{
    private readonly FleetOperationsHostFactory _host;
    private readonly HttpClient _fleetManager;
    private readonly HttpClient _operator;

    public MaintenanceAndAvailabilityContractTests(FleetOperationsHostFactory host)
    {
        _host = host;
        _host.Bus.Reset(static _ => throw new InvalidOperationException("Each test sets its response."));
        _fleetManager = host.Client(host.Token(FleetManagerRole));
        _operator = host.Client(host.Token(OperatorRole));
    }

    private static Exception Thrown(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected the action to throw.");
    }

    [Fact]
    public async Task Start_sends_the_route_id_and_nothing_else()
    {
        var id = Guid.CreateVersion7();
        _host.Bus.Reset(_ => Result<VehicleView>.Success(VehicleHttp.SampleView(id)));

        var response = await _fleetManager.PostAsync($"/api/fleet/vehicles/{id}/maintenance/start", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new StartMaintenance(id), Assert.Single(_host.Bus.Sent));
    }

    [Fact]
    public async Task Complete_sends_the_route_id_and_nothing_else()
    {
        var id = Guid.CreateVersion7();
        _host.Bus.Reset(_ => Result<VehicleView>.Success(VehicleHttp.SampleView(id)));

        var response = await _fleetManager.PostAsync($"/api/fleet/vehicles/{id}/maintenance/complete", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new CompleteMaintenance(id), Assert.Single(_host.Bus.Sent));
    }

    [Theory]
    [InlineData("start")]
    [InlineData("complete")]
    public async Task An_unknown_vehicle_is_404(string action)
    {
        _host.Bus.Reset(_ => Result<VehicleView>.FromFailure(FleetFailures.VehicleNotFound()));

        var response = await _fleetManager.PostAsync($"/api/fleet/vehicles/{VehicleHttp.VehicleId}/maintenance/{action}", null);

        await VehicleHttp.AssertProblem(response, HttpStatusCode.NotFound, "fleet", "VEHICLE_NOT_FOUND");
    }

    [Fact]
    public async Task Starting_maintenance_twice_is_422_with_the_rule_code_and_its_text()
    {
        _host.Bus.Reset(_ => throw Thrown(() =>
            BusinessRules.Check(new VehicleAlreadyUnderMaintenanceRule(MaintenanceStatus.UnderMaintenance))));

        var response = await _fleetManager.PostAsync($"/api/fleet/vehicles/{VehicleHttp.VehicleId}/maintenance/start", null);

        await VehicleHttp.AssertProblem(response, HttpStatusCode.UnprocessableEntity, "fleet", "VEHICLE_ALREADY_UNDER_MAINTENANCE");
        Assert.Equal(
            "The vehicle is already under maintenance.",
            (await VehicleHttp.ProblemOf(response)).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Starting_maintenance_on_a_committed_vehicle_is_422_with_the_commitment_code()
    {
        // Decision 3: the same rule that refuses deactivation refuses maintenance.
        _host.Bus.Reset(_ => throw Thrown(() => BusinessRules.Check(new VehicleHasMissionCommitmentRule(Guid.CreateVersion7()))));

        var response = await _fleetManager.PostAsync($"/api/fleet/vehicles/{VehicleHttp.VehicleId}/maintenance/start", null);

        await VehicleHttp.AssertProblem(response, HttpStatusCode.UnprocessableEntity, "fleet", "VEHICLE_HAS_MISSION_COMMITMENT");
    }

    [Fact]
    public async Task Completing_maintenance_that_was_not_started_is_422_with_the_rule_code_and_its_text()
    {
        _host.Bus.Reset(_ => throw Thrown(() =>
            BusinessRules.Check(new VehicleNotUnderMaintenanceRule(MaintenanceStatus.NotUnderMaintenance))));

        var response = await _fleetManager.PostAsync($"/api/fleet/vehicles/{VehicleHttp.VehicleId}/maintenance/complete", null);

        await VehicleHttp.AssertProblem(response, HttpStatusCode.UnprocessableEntity, "fleet", "VEHICLE_NOT_UNDER_MAINTENANCE");
        Assert.Equal(
            "The vehicle is not under maintenance.",
            (await VehicleHttp.ProblemOf(response)).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task A_lost_concurrency_race_on_start_maintenance_is_the_host_wide_409()
    {
        _host.Bus.Reset(_ => throw new Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException("The row was changed."));

        var response = await _fleetManager.PostAsync($"/api/fleet/vehicles/{VehicleHttp.VehicleId}/maintenance/start", null);

        await VehicleHttp.AssertProblem(response, HttpStatusCode.Conflict, "fleetoperations", "CONCURRENCY_CONFLICT");
    }

    [Fact]
    public async Task A_maintenance_route_id_that_is_not_a_guid_matches_no_endpoint()
    {
        var response = await _fleetManager.PostAsync("/api/fleet/vehicles/not-a-guid/maintenance/start", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(_host.Bus.Sent);
    }

    [Fact]
    public async Task The_guid_constraint_does_not_let_the_vehicle_route_swallow_available()
    {
        // Real route matching through the host, not a guess: /vehicles/available reaches the query, and
        // the id route is never selected for it, so no GetVehicle("available") is ever built.
        _host.Bus.Reset(static _ => (IReadOnlyList<AvailableVehicleView>)[]);

        var response = await _operator.GetAsync("/api/fleet/vehicles/available");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = Assert.Single(_host.Bus.Sent);
        Assert.IsType<GetAvailableVehicles>(sent);
        Assert.DoesNotContain(_host.Bus.Sent, message => message is GetVehicle);
    }

    [Fact]
    public async Task The_available_list_is_returned_with_enum_names()
    {
        // F-9: every REST body names enum members as strings, this one included.
        var id = Guid.CreateVersion7();
        _host.Bus.Reset(_ => (IReadOnlyList<AvailableVehicleView>)
            [new AvailableVehicleView(id, "AB-7", VehicleType.HeavyTruck, 12000.5m)]);

        var response = await _operator.GetAsync("/api/fleet/vehicles/available");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var row = Assert.Single(body.RootElement.EnumerateArray());
        Assert.Equal(id, row.GetProperty("id").GetGuid());
        Assert.Equal("AB-7", row.GetProperty("plateNumber").GetString());
        Assert.Equal("HeavyTruck", row.GetProperty("vehicleType").GetString());
        Assert.Equal(12000.5m, row.GetProperty("capacityKg").GetDecimal());
        // The available view carries no status: every row in the list is available by construction.
        Assert.False(row.TryGetProperty("operationalStatus", out _));
    }

    [Fact]
    public async Task An_empty_fleet_is_an_empty_json_array()
    {
        _host.Bus.Reset(static _ => (IReadOnlyList<AvailableVehicleView>)[]);

        var response = await _operator.GetAsync("/api/fleet/vehicles/available");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[]", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_available_list_is_a_GET_and_nothing_else()
    {
        // A read is safe (RFC 9110): the query is the only thing the GET sends and no verb was added.
        _host.Bus.Reset(static _ => (IReadOnlyList<AvailableVehicleView>)[]);

        var response = await _operator.PostAsync("/api/fleet/vehicles/available", null);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Empty(_host.Bus.Sent);
    }
}
