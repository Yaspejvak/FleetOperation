using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fserp.FleetOperations.Modules.Fleet.Application;
using Fserp.FleetOperations.Modules.Fleet.Application.Commands;
using Fserp.FleetOperations.Modules.Fleet.Application.Queries;
using Fserp.FleetOperations.Modules.Fleet.Application.Views;
using Fserp.FleetOperations.Modules.Fleet.Contracts;
using Fserp.FleetOperations.Modules.Fleet.Domain;
using Fserp.FleetOperations.Modules.Fleet.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using MPCore.Application.Results;
using MPCore.Domain.Rules;
using Npgsql;
using static Fserp.FleetOperations.UnitTests.Host.FleetOperationsHostFactory;

namespace Fserp.FleetOperations.UnitTests.Host;

/// <summary>Requests and assertions shared by the host tests.</summary>
internal static class VehicleHttp
{
    public static readonly Guid VehicleId = Guid.Parse("01920000-0000-7000-8000-000000000001");

    public static VehicleView SampleView(Guid id) =>
        new(id, "AB-1", VehicleType.Truck, 1200m, OperationalStatus.Active, MaintenanceStatus.NotUnderMaintenance, VehicleDisplayStatus.Active, null);

    public static HttpRequestMessage Request(string endpoint, Guid? id = null) => endpoint switch
    {
        "register" => new HttpRequestMessage(HttpMethod.Post, "/api/fleet/vehicles")
        {
            Content = Json("{\"plateNumber\":\"ab-1\",\"vehicleType\":\"Truck\",\"capacityKg\":1200}"),
        },
        "change-status" => new HttpRequestMessage(HttpMethod.Put, $"/api/fleet/vehicles/{id ?? VehicleId}/status")
        {
            Content = Json("{\"status\":\"Inactive\"}"),
        },
        "get" => new HttpRequestMessage(HttpMethod.Get, $"/api/fleet/vehicles/{id ?? VehicleId}"),
        _ => throw new ArgumentOutOfRangeException(nameof(endpoint)),
    };

    public static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    public static async Task<JsonElement> ProblemOf(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    public static async Task AssertProblem(HttpResponseMessage response, HttpStatusCode status, string domain, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = await ProblemOf(response);
        Assert.Equal((int)status, problem.GetProperty("status").GetInt32());
        Assert.Equal(domain, problem.GetProperty("errorDomain").GetString());
        Assert.Equal(code, problem.GetProperty("errorCode").GetString());
        Assert.Equal($"urn:mpcore:error:{domain}:{code}", problem.GetProperty("type").GetString());
    }
}

/// <summary>
/// The three round 1 endpoints through the real host pipeline with real signed bearer tokens
/// (docs/architecture.md, decision 5, "Tests": no token 401, a wrong role 403, the right role succeeds).
/// A refused request never reaches the bus. gRPC has no Fleet service until round 4.
/// </summary>
public sealed class VehicleEndpointAuthorizationTests : IClassFixture<FleetOperationsHostFactory>
{
    private readonly FleetOperationsHostFactory _host;

    public VehicleEndpointAuthorizationTests(FleetOperationsHostFactory host)
    {
        _host = host;
        _host.Bus.Reset(static message => Result<VehicleView>.Success(VehicleHttp.SampleView(message switch
        {
            GetVehicle query => query.VehicleId,
            ChangeVehicleStatus command => command.VehicleId,
            _ => VehicleHttp.VehicleId,
        })));
    }

    public static TheoryData<string> Endpoints() => ["register", "change-status", "get"];

    private async Task AssertRefused(string endpoint, string? token, HttpStatusCode expected, string expectedCode)
    {
        var response = await _host.Client(token).SendAsync(VehicleHttp.Request(endpoint));

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
    public Task A_wrong_role_is_403_and_never_reaches_the_bus(string endpoint, string role) =>
        AssertRefused(endpoint, _host.Token(role), HttpStatusCode.Forbidden, "FORBIDDEN");

    [Theory]
    [MemberData(nameof(Endpoints))]
    public Task A_valid_token_without_any_role_is_403(string endpoint) =>
        AssertRefused(endpoint, _host.Token(), HttpStatusCode.Forbidden, "FORBIDDEN");

    [Theory]
    [InlineData("register", FleetManagerRole, HttpStatusCode.Created)]
    [InlineData("change-status", FleetManagerRole, HttpStatusCode.OK)]
    [InlineData("get", OperatorRole, HttpStatusCode.OK)]
    [InlineData("get", FleetManagerRole, HttpStatusCode.OK)]
    public async Task The_right_role_reaches_the_command_or_query(string endpoint, string role, HttpStatusCode expected)
    {
        var response = await _host.Client(_host.Token(role)).SendAsync(VehicleHttp.Request(endpoint));

        Assert.Equal(expected, response.StatusCode);
        var sent = Assert.Single(_host.Bus.Sent);
        Assert.IsType(
            endpoint switch
            {
                "register" => typeof(RegisterVehicle),
                "change-status" => typeof(ChangeVehicleStatus),
                _ => typeof(GetVehicle),
            },
            sent);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public Task A_token_signed_by_another_key_is_401(string endpoint) =>
        AssertRefused(
            endpoint,
            _host.Sign(
                new TokenShape { RealmRoles = [FleetManagerRole, OperatorRole] },
                new RsaSecurityKey(RSA.Create(2048)) { KeyId = "fleet-ops-test-key" }),
            HttpStatusCode.Unauthorized,
            "UNAUTHENTICATED");

    [Fact]
    public Task A_token_for_another_audience_is_401() =>
        AssertRefused(
            "register",
            _host.Sign(new TokenShape { RealmRoles = [FleetManagerRole], Audience = "another-api" }),
            HttpStatusCode.Unauthorized,
            "UNAUTHENTICATED");

    [Fact]
    public Task A_token_from_another_issuer_is_401() =>
        AssertRefused(
            "register",
            _host.Sign(new TokenShape { RealmRoles = [FleetManagerRole], Issuer = "https://elsewhere.test.invalid/realms/x" }),
            HttpStatusCode.Unauthorized,
            "UNAUTHENTICATED");

    [Fact]
    public Task An_expired_token_is_401() =>
        AssertRefused(
            "register",
            _host.Sign(new TokenShape { RealmRoles = [FleetManagerRole], Expired = true }),
            HttpStatusCode.Unauthorized,
            "UNAUTHENTICATED");

    [Fact]
    public Task A_top_level_role_claim_grants_nothing() =>
        // docs/architecture.md: "A top-level role claim in a token is discarded by design."
        AssertRefused(
            "register",
            _host.Sign(new TokenShape
            {
                RealmRoles = [OperatorRole],
                ExtraClaims = { ["role"] = FleetManagerRole, ["roles"] = new[] { FleetManagerRole } },
            }),
            HttpStatusCode.Forbidden,
            "FORBIDDEN");

    [Fact]
    public async Task Identity_headers_do_not_raise_an_operator_to_fleet_manager()
    {
        // CLAUDE.md: identity never comes from an arbitrary header.
        var request = VehicleHttp.Request("register");
        request.Headers.Add("X-Forwarded-User", "someone-else");
        request.Headers.Add("X-Forwarded-Roles", FleetManagerRole);
        request.Headers.Add("X-User-Roles", FleetManagerRole);

        var response = await _host.Client(_host.Token(OperatorRole)).SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_host.Bus.Sent);
    }
}

/// <summary>
/// The REST contract of the slice (docs/plans/fleet.md, "REST status codes"): what each endpoint sends
/// to the bus, and how each outcome reaches the caller as a status and an RFC 9457 problem under the
/// rule's own domain and code. The outcome is chosen through the recording bus; the handlers that
/// produce those outcomes are covered by the handler tests.
/// </summary>
public sealed class VehicleEndpointContractTests : IClassFixture<FleetOperationsHostFactory>
{
    private readonly FleetOperationsHostFactory _host;
    private readonly HttpClient _fleetManager;
    private readonly HttpClient _operator;

    public VehicleEndpointContractTests(FleetOperationsHostFactory host)
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

    private static DbUpdateException UniqueViolationOn(string index) =>
        new(
            "save failed",
            new PostgresException("duplicate key value violates unique constraint", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation, constraintName: index));

    [Fact]
    public async Task Register_is_201_with_the_location_of_the_new_vehicle_and_enum_names_in_the_body()
    {
        var id = Guid.CreateVersion7();
        _host.Bus.Reset(_ => Result<VehicleView>.Success(VehicleHttp.SampleView(id)));

        var response = await _fleetManager.PostAsync(
            "/api/fleet/vehicles",
            VehicleHttp.Json("{\"plateNumber\":\" ab-1 \",\"vehicleType\":\"HeavyTruck\",\"capacityKg\":1250.5}"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.EndsWith($"/api/fleet/vehicles/{id}", response.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Truck", body.RootElement.GetProperty("vehicleType").GetString());
        Assert.Equal("Active", body.RootElement.GetProperty("operationalStatus").GetString());
        Assert.Equal("NotUnderMaintenance", body.RootElement.GetProperty("maintenanceStatus").GetString());
        Assert.Equal("Active", body.RootElement.GetProperty("displayStatus").GetString());

        // The command carries what the caller sent and nothing about the caller.
        Assert.Equal(new RegisterVehicle(" ab-1 ", VehicleType.HeavyTruck, 1250.5m), Assert.Single(_host.Bus.Sent));
    }

    [Fact]
    public async Task Register_without_a_plate_sends_an_empty_plate_for_the_validator_to_refuse()
    {
        _host.Bus.Reset(_ => Result<VehicleView>.Success(VehicleHttp.SampleView(Guid.CreateVersion7())));

        await _fleetManager.PostAsync("/api/fleet/vehicles", VehicleHttp.Json("{\"vehicleType\":\"Van\",\"capacityKg\":1}"));

        Assert.Equal(new RegisterVehicle(string.Empty, VehicleType.Van, 1m), Assert.Single(_host.Bus.Sent));
    }

    [Fact]
    public async Task A_plate_already_registered_is_409_with_the_plate_code()
    {
        _host.Bus.Reset(_ => Result<VehicleView>.FromFailure(FleetFailures.PlateNumberAlreadyRegistered()));

        var response = await _fleetManager.SendAsync(VehicleHttp.Request("register"));

        await VehicleHttp.AssertProblem(response, HttpStatusCode.Conflict, "fleet", "VEHICLE_PLATE_NUMBER_ALREADY_REGISTERED");
    }

    [Fact]
    public async Task A_plate_refused_by_the_unique_index_at_commit_is_the_same_409()
    {
        // F-2: the race the pre-check cannot close. Pins the AddHttpExceptionMapper registration in Program.
        _host.Bus.Reset(_ => throw UniqueViolationOn("ux_vehicles_plate_number"));

        var response = await _fleetManager.SendAsync(VehicleHttp.Request("register"));

        await VehicleHttp.AssertProblem(response, HttpStatusCode.Conflict, "fleet", "VEHICLE_PLATE_NUMBER_ALREADY_REGISTERED");
    }

    [Fact]
    public async Task A_unique_violation_on_an_undeclared_index_stays_a_500()
    {
        _host.Bus.Reset(_ => throw UniqueViolationOn("ux_something_else"));

        var response = await _fleetManager.SendAsync(VehicleHttp.Request("register"));

        await VehicleHttp.AssertProblem(response, HttpStatusCode.InternalServerError, "mpcore.http", "UNEXPECTED_FAILURE");
    }

    [Fact]
    public async Task Deactivating_a_committed_vehicle_is_422_with_the_rule_code_and_its_text()
    {
        // F-3.
        _host.Bus.Reset(_ => throw Thrown(() => BusinessRules.Check(new VehicleHasMissionCommitmentRule(Guid.CreateVersion7()))));

        var response = await _fleetManager.SendAsync(VehicleHttp.Request("change-status"));

        await VehicleHttp.AssertProblem(response, HttpStatusCode.UnprocessableEntity, "fleet", "VEHICLE_HAS_MISSION_COMMITMENT");
        Assert.Equal(
            "The vehicle is committed to a mission.",
            (await VehicleHttp.ProblemOf(response)).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Change_status_sends_the_route_id_and_the_requested_status()
    {
        var id = Guid.CreateVersion7();
        _host.Bus.Reset(_ => Result<VehicleView>.Success(VehicleHttp.SampleView(id)));

        var response = await _fleetManager.SendAsync(VehicleHttp.Request("change-status", id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new ChangeVehicleStatus(id, OperationalStatus.Inactive), Assert.Single(_host.Bus.Sent));
    }

    [Fact]
    public async Task Change_status_of_an_unknown_vehicle_is_404()
    {
        _host.Bus.Reset(_ => Result<VehicleView>.FromFailure(FleetFailures.VehicleNotFound()));

        var response = await _fleetManager.SendAsync(VehicleHttp.Request("change-status"));

        await VehicleHttp.AssertProblem(response, HttpStatusCode.NotFound, "fleet", "VEHICLE_NOT_FOUND");
    }

    [Fact]
    public async Task A_lost_concurrency_race_on_change_status_is_409()
    {
        // docs/plans/fleet.md, "REST status codes": 409 lost concurrency. docs/plans/README.md, "Failure
        // mapping": lost optimistic concurrency 409. The exact code is O-9 (round 7); the status is decided.
        _host.Bus.Reset(_ => throw new DbUpdateConcurrencyException("The row was changed by another request."));

        var response = await _fleetManager.SendAsync(VehicleHttp.Request("change-status"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task A_lost_concurrency_race_is_the_host_wide_conflict_with_its_text()
    {
        // Decided (lead), round 1 closure: fleetoperations/CONCURRENCY_CONFLICT, category Concurrency.
        // The text proves the host resource file is registered in the message catalog.
        _host.Bus.Reset(_ => throw new DbUpdateConcurrencyException("The row was changed by another request."));

        var response = await _fleetManager.SendAsync(VehicleHttp.Request("change-status"));

        await VehicleHttp.AssertProblem(response, HttpStatusCode.Conflict, "fleetoperations", "CONCURRENCY_CONFLICT");
        var problem = await VehicleHttp.ProblemOf(response);
        Assert.Equal("Concurrency", problem.GetProperty("category").GetString());
        Assert.Equal("The record was changed by another request. Read it again and retry.", problem.GetProperty("detail").GetString());
        // The exception's own message never reaches the caller.
        Assert.DoesNotContain("The row was changed", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_plate_refused_by_the_value_object_for_its_length_is_422_with_the_rule_code_and_its_text()
    {
        // The value object is the enforcement when the validator is bypassed.
        _host.Bus.Reset(_ => throw Thrown(() => PlateNumber.Create("ABCDEFGHIJ-123456")));

        var response = await _fleetManager.SendAsync(VehicleHttp.Request("register"));

        await VehicleHttp.AssertProblem(response, HttpStatusCode.UnprocessableEntity, "fleet", "VEHICLE_PLATE_NUMBER_TOO_LONG");
        Assert.Equal(
            "The plate number must be at most 16 characters.",
            (await VehicleHttp.ProblemOf(response)).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Get_an_unknown_vehicle_is_404()
    {
        _host.Bus.Reset(_ => Result<VehicleView>.FromFailure(FleetFailures.VehicleNotFound()));

        var response = await _operator.SendAsync(VehicleHttp.Request("get"));

        await VehicleHttp.AssertProblem(response, HttpStatusCode.NotFound, "fleet", "VEHICLE_NOT_FOUND");
    }

    [Fact]
    public async Task Get_returns_the_view()
    {
        var id = Guid.CreateVersion7();
        _host.Bus.Reset(_ => Result<VehicleView>.Success(VehicleHttp.SampleView(id)));

        var response = await _operator.SendAsync(VehicleHttp.Request("get", id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(id, body.RootElement.GetProperty("id").GetGuid());
        Assert.Equal("AB-1", body.RootElement.GetProperty("plateNumber").GetString());
        Assert.Equal(1200m, body.RootElement.GetProperty("capacityKg").GetDecimal());
        Assert.Equal(new GetVehicle(id), Assert.Single(_host.Bus.Sent));
    }

    [Fact]
    public async Task A_route_id_that_is_not_a_guid_matches_no_endpoint()
    {
        var response = await _operator.GetAsync("/api/fleet/vehicles/not-a-guid");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(_host.Bus.Sent);
    }

    [Theory]
    [InlineData("{\"plateNumber\":\"A\",\"vehicleType\":1,\"capacityKg\":1}")]
    [InlineData("{\"plateNumber\":\"A\",\"vehicleType\":\"Car\",\"capacityKg\":1}")]
    [InlineData("")]
    public async Task A_body_that_cannot_be_read_is_400_and_never_reaches_the_bus(string body)
    {
        // Owner decision (round 1 closure, finding 3): an unreadable body is a problem document, MP Core's
        // own 400 mpcore.http/MALFORMED_REQUEST, never a bare 400 and never a message on the bus.
        var response = await _fleetManager.PostAsync("/api/fleet/vehicles", VehicleHttp.Json(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_host.Bus.Sent);
        await VehicleHttp.AssertProblem(response, HttpStatusCode.BadRequest, "mpcore.http", "MALFORMED_REQUEST");
        // No parser text (JSON path, enum name, exception message) is disclosed.
        var detail = (await VehicleHttp.ProblemOf(response)).GetProperty("detail").GetString();
        Assert.DoesNotContain("vehicleType", detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Car", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("JSON", detail, StringComparison.OrdinalIgnoreCase);
    }
}
