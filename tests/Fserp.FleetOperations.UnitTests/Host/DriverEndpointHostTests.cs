using System.Net;
using System.Text.Json;
using Fserp.FleetOperations.Modules.Drivers.Application;
using Fserp.FleetOperations.Modules.Drivers.Application.Commands;
using Fserp.FleetOperations.Modules.Drivers.Application.Queries;
using Fserp.FleetOperations.Modules.Drivers.Application.Views;
using Fserp.FleetOperations.Modules.Drivers.Domain;
using Fserp.FleetOperations.Modules.Drivers.Domain.Rules;
using Fserp.FleetOperations.Modules.Fleet.Contracts;
using Microsoft.EntityFrameworkCore;
using MPCore.Application.Results;
using MPCore.Domain.Rules;
using static Fserp.FleetOperations.UnitTests.Host.FleetOperationsHostFactory;

namespace Fserp.FleetOperations.UnitTests.Host;

/// <summary>Requests and sample values shared by the Drivers host tests.</summary>
internal static class DriverHttp
{
    public static readonly Guid DriverId = Guid.Parse("01920000-0000-7000-8000-0000000000d1");

    public static DriverView SampleView(Guid id) =>
        new(id, "Ada Lovelace", OperationalStatus.Active, [VehicleType.Van], null);

    public static HttpRequestMessage Request(string endpoint, Guid? id = null) => endpoint switch
    {
        "register" => new HttpRequestMessage(HttpMethod.Post, "/api/drivers")
        {
            Content = VehicleHttp.Json("{\"fullName\":\"Ada Lovelace\",\"vehicleTypes\":[\"Van\"]}"),
        },
        "change-status" => new HttpRequestMessage(HttpMethod.Put, $"/api/drivers/{id ?? DriverId}/status")
        {
            Content = VehicleHttp.Json("{\"status\":\"Inactive\"}"),
        },
        "get" => new HttpRequestMessage(HttpMethod.Get, $"/api/drivers/{id ?? DriverId}"),
        "available" => new HttpRequestMessage(HttpMethod.Get, "/api/drivers/available"),
        _ => throw new ArgumentOutOfRangeException(nameof(endpoint)),
    };
}

/// <summary>
/// The four Drivers endpoints through the real host pipeline with real signed bearer tokens
/// (docs/architecture.md, decision 5, and D-3: the fleet manager registers and manages drivers, every read
/// is open to the operational reader). A refused request never reaches the bus.
/// </summary>
public sealed class DriverEndpointAuthorizationTests : IClassFixture<FleetOperationsHostFactory>
{
    private readonly FleetOperationsHostFactory _host;

    public DriverEndpointAuthorizationTests(FleetOperationsHostFactory host)
    {
        _host = host;
        _host.Bus.Reset(static message => message switch
        {
            GetAvailableDrivers => (object)Array.Empty<AvailableDriverView>(),
            GetDriver query => Result<DriverView>.Success(DriverHttp.SampleView(query.DriverId)),
            ChangeDriverStatus command => Result<DriverView>.Success(DriverHttp.SampleView(command.DriverId)),
            _ => Result<DriverView>.Success(DriverHttp.SampleView(DriverHttp.DriverId)),
        });
    }

    public static TheoryData<string> Endpoints() => ["register", "change-status", "get", "available"];

    private async Task AssertRefused(string endpoint, string? token, HttpStatusCode expected, string expectedCode)
    {
        var response = await _host.Client(token).SendAsync(DriverHttp.Request(endpoint));

        await VehicleHttp.AssertProblem(response, expected, "mpcore.security", expectedCode);
        Assert.Empty(_host.Bus.Sent);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public Task No_token_is_401_and_never_reaches_the_bus(string endpoint) =>
        AssertRefused(endpoint, null, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");

    [Theory]
    [InlineData("register", OperatorRole)]
    [InlineData("register", AdministratorRole)]
    [InlineData("change-status", OperatorRole)]
    [InlineData("change-status", AdministratorRole)]
    [InlineData("get", AdministratorRole)]
    [InlineData("available", AdministratorRole)]
    public Task A_wrong_role_is_403_and_never_reaches_the_bus(string endpoint, string role) =>
        // An operator may not register or manage drivers (D-3); an administrator reads the audit trail
        // only and is not a superset role (AD-1).
        AssertRefused(endpoint, _host.Token(role), HttpStatusCode.Forbidden, "FORBIDDEN");

    [Theory]
    [MemberData(nameof(Endpoints))]
    public Task A_valid_token_without_any_role_is_403(string endpoint) =>
        AssertRefused(endpoint, _host.Token(), HttpStatusCode.Forbidden, "FORBIDDEN");

    [Theory]
    [InlineData("register", FleetManagerRole, HttpStatusCode.Created, typeof(RegisterDriver))]
    [InlineData("change-status", FleetManagerRole, HttpStatusCode.OK, typeof(ChangeDriverStatus))]
    [InlineData("get", OperatorRole, HttpStatusCode.OK, typeof(GetDriver))]
    [InlineData("get", FleetManagerRole, HttpStatusCode.OK, typeof(GetDriver))]
    [InlineData("available", OperatorRole, HttpStatusCode.OK, typeof(GetAvailableDrivers))]
    [InlineData("available", FleetManagerRole, HttpStatusCode.OK, typeof(GetAvailableDrivers))]
    public async Task The_right_role_reaches_the_command_or_query(
        string endpoint, string role, HttpStatusCode expected, Type message)
    {
        var response = await _host.Client(_host.Token(role)).SendAsync(DriverHttp.Request(endpoint));

        Assert.Equal(expected, response.StatusCode);
        Assert.IsType(message, Assert.Single(_host.Bus.Sent));
    }

    [Fact]
    public async Task Identity_headers_do_not_raise_an_operator_to_fleet_manager()
    {
        // CLAUDE.md: identity never comes from an arbitrary header.
        var request = DriverHttp.Request("register");
        request.Headers.Add("X-Forwarded-User", "someone-else");
        request.Headers.Add("X-Forwarded-Roles", FleetManagerRole);

        var response = await _host.Client(_host.Token(OperatorRole)).SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_host.Bus.Sent);
    }
}

/// <summary>
/// The REST contract of the Drivers module: what each endpoint sends to the bus, how the <c>:guid</c>
/// constraint keeps <c>{driverId}</c> from shadowing <c>/available</c>, and how each outcome reaches the
/// caller as a status and an RFC 9457 problem under the rule's own domain and code.
/// </summary>
public sealed class DriverEndpointContractTests : IClassFixture<FleetOperationsHostFactory>
{
    private readonly FleetOperationsHostFactory _host;
    private readonly HttpClient _fleetManager;
    private readonly HttpClient _operator;

    public DriverEndpointContractTests(FleetOperationsHostFactory host)
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
    public async Task Register_is_201_with_the_location_of_the_new_driver_and_enum_names_in_the_body()
    {
        var id = Guid.CreateVersion7();
        _host.Bus.Reset(_ => Result<DriverView>.Success(
            new DriverView(id, "Ada Lovelace", OperationalStatus.Active, [VehicleType.Van, VehicleType.HeavyTruck], null)));

        var response = await _fleetManager.PostAsync(
            "/api/drivers",
            VehicleHttp.Json("{\"fullName\":\"  Ada Lovelace  \",\"vehicleTypes\":[\"Van\",\"HeavyTruck\"]}"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.EndsWith($"/api/drivers/{id}", response.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Ada Lovelace", body.RootElement.GetProperty("fullName").GetString());
        Assert.Equal("Active", body.RootElement.GetProperty("operationalStatus").GetString());
        // F-9: every REST body names enum members as strings, in requests and responses.
        Assert.Equal(
            ["Van", "HeavyTruck"],
            body.RootElement.GetProperty("qualifiedVehicleTypes").EnumerateArray().Select(element => element.GetString()));

        // The command carries what the caller sent and nothing about the caller.
        var sent = Assert.IsType<RegisterDriver>(Assert.Single(_host.Bus.Sent));
        Assert.Equal("  Ada Lovelace  ", sent.FullName);
        Assert.Equal([VehicleType.Van, VehicleType.HeavyTruck], sent.VehicleTypes);
    }

    [Fact]
    public async Task Register_without_a_name_or_a_list_sends_the_empty_values_for_the_validator_to_refuse()
    {
        _host.Bus.Reset(_ => Result<DriverView>.Success(DriverHttp.SampleView(Guid.CreateVersion7())));

        await _fleetManager.PostAsync("/api/drivers", VehicleHttp.Json("{}"));

        var sent = Assert.IsType<RegisterDriver>(Assert.Single(_host.Bus.Sent));
        Assert.Equal(string.Empty, sent.FullName);
        Assert.Empty(sent.VehicleTypes);
    }

    [Fact]
    public async Task Change_status_sends_the_route_id_and_the_requested_status()
    {
        var id = Guid.CreateVersion7();
        _host.Bus.Reset(_ => Result<DriverView>.Success(DriverHttp.SampleView(id)));

        var response = await _fleetManager.SendAsync(DriverHttp.Request("change-status", id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new ChangeDriverStatus(id, OperationalStatus.Inactive), Assert.Single(_host.Bus.Sent));
    }

    [Fact]
    public async Task Change_status_of_an_unknown_driver_is_404()
    {
        _host.Bus.Reset(_ => Result<DriverView>.FromFailure(DriversFailures.DriverNotFound()));

        var response = await _fleetManager.SendAsync(DriverHttp.Request("change-status"));

        await VehicleHttp.AssertProblem(response, HttpStatusCode.NotFound, "drivers", "DRIVER_NOT_FOUND");
    }

    [Fact]
    public async Task Deactivating_a_committed_driver_is_422_with_the_rule_code_and_its_text()
    {
        // D-2. The text proves the Drivers resource file is registered in the host's message catalog.
        _host.Bus.Reset(_ => throw Thrown(() => BusinessRules.Check(new DriverHasMissionCommitmentRule(Guid.CreateVersion7()))));

        var response = await _fleetManager.SendAsync(DriverHttp.Request("change-status"));

        await VehicleHttp.AssertProblem(response, HttpStatusCode.UnprocessableEntity, "drivers", "DRIVER_HAS_MISSION_COMMITMENT");
        Assert.Equal(
            "The driver is committed to a mission.",
            (await VehicleHttp.ProblemOf(response)).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task A_registration_without_a_qualification_is_422_with_the_rule_code_and_its_text()
    {
        // D-5, when the validator is bypassed: the aggregate is the enforcement.
        _host.Bus.Reset(_ => throw Thrown(() => BusinessRules.Check(new DriverQualificationRequiredRule([]))));

        var response = await _fleetManager.SendAsync(DriverHttp.Request("register"));

        await VehicleHttp.AssertProblem(response, HttpStatusCode.UnprocessableEntity, "drivers", "DRIVER_QUALIFICATION_REQUIRED");
        Assert.Equal(
            "A driver needs at least one vehicle type.",
            (await VehicleHttp.ProblemOf(response)).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task A_lost_concurrency_race_on_a_driver_is_the_host_wide_409()
    {
        // L-1: the concurrency failure is host-wide, not Fleet's, because a lost race can happen on any
        // module's aggregate. This is the first evidence of that for a second module.
        _host.Bus.Reset(_ => throw new DbUpdateConcurrencyException("The row was changed by another request."));

        var response = await _fleetManager.SendAsync(DriverHttp.Request("change-status"));

        await VehicleHttp.AssertProblem(response, HttpStatusCode.Conflict, "fleetoperations", "CONCURRENCY_CONFLICT");
        Assert.DoesNotContain("The row was changed", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_an_unknown_driver_is_404()
    {
        _host.Bus.Reset(_ => Result<DriverView>.FromFailure(DriversFailures.DriverNotFound()));

        var response = await _operator.SendAsync(DriverHttp.Request("get"));

        await VehicleHttp.AssertProblem(response, HttpStatusCode.NotFound, "drivers", "DRIVER_NOT_FOUND");
    }

    [Fact]
    public async Task Get_returns_the_view()
    {
        var id = Guid.CreateVersion7();
        _host.Bus.Reset(_ => Result<DriverView>.Success(DriverHttp.SampleView(id)));

        var response = await _operator.SendAsync(DriverHttp.Request("get", id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(id, body.RootElement.GetProperty("id").GetGuid());
        Assert.Equal("Ada Lovelace", body.RootElement.GetProperty("fullName").GetString());
        Assert.Equal(new GetDriver(id), Assert.Single(_host.Bus.Sent));
    }

    [Fact]
    public async Task The_available_route_reaches_GetAvailableDrivers_and_not_GetDriver()
    {
        // The routing guarantee: /api/drivers/available and /api/drivers/{driverId:guid} sit at the same
        // depth, and the constraint is what sends "available" to the literal route. Measured through the
        // real matcher, not inferred from the pattern.
        var one = new AvailableDriverView(Guid.CreateVersion7(), "Ada Lovelace", [VehicleType.Van]);
        _host.Bus.Reset(_ => (IReadOnlyList<AvailableDriverView>)new[] { one });

        var response = await _operator.SendAsync(DriverHttp.Request("available"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.IsType<GetAvailableDrivers>(Assert.Single(_host.Bus.Sent));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var row = Assert.Single(body.RootElement.EnumerateArray());
        Assert.Equal("Ada Lovelace", row.GetProperty("fullName").GetString());
        Assert.Equal("Van", Assert.Single(row.GetProperty("qualifiedVehicleTypes").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task An_empty_available_list_is_200_with_an_empty_array_and_not_404()
    {
        _host.Bus.Reset(_ => (IReadOnlyList<AvailableDriverView>)Array.Empty<AvailableDriverView>());

        var response = await _operator.SendAsync(DriverHttp.Request("available"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[]", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_route_id_that_is_not_a_guid_matches_no_endpoint()
    {
        var response = await _operator.GetAsync("/api/drivers/not-a-guid");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(_host.Bus.Sent);
    }

    [Theory]
    [InlineData("{\"fullName\":\"Ada\",\"vehicleTypes\":[1]}")]
    [InlineData("{\"fullName\":\"Ada\",\"vehicleTypes\":[\"Car\"]}")]
    [InlineData("")]
    public async Task A_body_that_cannot_be_read_is_400_and_never_reaches_the_bus(string body)
    {
        // F-9 and F-12: a number for an enum is refused, never bound, and an unreadable body answers the
        // host-wide MALFORMED_REQUEST problem rather than a bare 400.
        var response = await _fleetManager.PostAsync("/api/drivers", VehicleHttp.Json(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_host.Bus.Sent);
        await VehicleHttp.AssertProblem(response, HttpStatusCode.BadRequest, "mpcore.http", "MALFORMED_REQUEST");
    }
}
