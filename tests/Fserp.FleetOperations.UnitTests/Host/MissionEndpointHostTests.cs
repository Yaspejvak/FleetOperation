using System.Net;
using System.Text.Json;
using Fserp.FleetOperations.Modules.Fleet.Domain;
using Fserp.FleetOperations.Modules.Fleet.Domain.Rules;
using Fserp.FleetOperations.Modules.Operations.Application;
using Fserp.FleetOperations.Modules.Operations.Application.Commands;
using Fserp.FleetOperations.Modules.Operations.Application.Queries;
using Fserp.FleetOperations.Modules.Operations.Application.Views;
using Fserp.FleetOperations.Modules.Operations.Domain;
using Fserp.FleetOperations.Modules.Operations.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using MPCore.Application.Querying;
using MPCore.Application.Results;
using MPCore.Domain.Rules;
using Npgsql;
using static Fserp.FleetOperations.UnitTests.Host.FleetOperationsHostFactory;

namespace Fserp.FleetOperations.UnitTests.Host;

/// <summary>Requests and sample values shared by the Operations host tests.</summary>
internal static class MissionHttp
{
    public static readonly Guid MissionId = Guid.Parse("01920000-0000-7000-8000-0000000000e1");

    public static readonly Guid VehicleId = Guid.Parse("01920000-0000-7000-8000-0000000000e2");

    public static readonly Guid DriverId = Guid.Parse("01920000-0000-7000-8000-0000000000e3");

    public static readonly DateTimeOffset ScheduledAt = new(2026, 10, 6, 7, 30, 0, TimeSpan.Zero);

    public static MissionView SampleView(Guid id, MissionStatus status = MissionStatus.Scheduled) =>
        new(id, "Tehran", "Isfahan", 800m, ScheduledAt, status, null, null);

    public static HttpRequestMessage Request(string endpoint, Guid? id = null) => endpoint switch
    {
        "create" => new HttpRequestMessage(HttpMethod.Post, "/api/operations/missions")
        {
            Content = VehicleHttp.Json("{\"origin\":\"Tehran\",\"destination\":\"Isfahan\",\"requiredCapacityKg\":800}"),
        },
        "schedule" => new HttpRequestMessage(HttpMethod.Post, $"/api/operations/missions/{id ?? MissionId}/schedule")
        {
            Content = VehicleHttp.Json("{\"scheduledAt\":\"2026-10-06T07:30:00+00:00\"}"),
        },
        "assign" => new HttpRequestMessage(HttpMethod.Post, $"/api/operations/missions/{id ?? MissionId}/assign")
        {
            Content = VehicleHttp.Json($"{{\"vehicleId\":\"{VehicleId}\",\"driverId\":\"{DriverId}\"}}"),
        },
        "start" => new HttpRequestMessage(HttpMethod.Post, $"/api/operations/missions/{id ?? MissionId}/start"),
        "complete" => new HttpRequestMessage(HttpMethod.Post, $"/api/operations/missions/{id ?? MissionId}/complete"),
        "cancel" => new HttpRequestMessage(HttpMethod.Post, $"/api/operations/missions/{id ?? MissionId}/cancel"),
        "get" => new HttpRequestMessage(HttpMethod.Get, $"/api/operations/missions/{id ?? MissionId}"),
        "active" => new HttpRequestMessage(HttpMethod.Get, "/api/operations/missions/active"),
        _ => throw new ArgumentOutOfRangeException(nameof(endpoint)),
    };
}

/// <summary>
/// The eight Operations endpoints through the real host pipeline with real signed bearer tokens
/// (docs/plans/operations.md, "Authorization": the six commands are the operator's, both reads are open to
/// the operational reader). A refused request never reaches the bus.
/// </summary>
public sealed class MissionEndpointAuthorizationTests : IClassFixture<FleetOperationsHostFactory>
{
    private readonly FleetOperationsHostFactory _host;

    public MissionEndpointAuthorizationTests(FleetOperationsHostFactory host)
    {
        _host = host;
        _host.Bus.Reset(static message => message switch
        {
            GetActiveMissions query => (object)new Page<MissionView>([], query.Page.Number, query.Page.Size, 0),
            GetMission query => Result<MissionView>.Success(MissionHttp.SampleView(query.MissionId)),
            _ => Result<MissionView>.Success(MissionHttp.SampleView(MissionHttp.MissionId)),
        });
    }

    public static TheoryData<string> Endpoints() =>
        ["create", "schedule", "assign", "start", "complete", "cancel", "get", "active"];

    public static TheoryData<string> Commands() => ["create", "schedule", "assign", "start", "complete", "cancel"];

    private async Task AssertRefused(string endpoint, string? token, HttpStatusCode expected, string expectedCode)
    {
        var response = await _host.Client(token).SendAsync(MissionHttp.Request(endpoint));

        await VehicleHttp.AssertProblem(response, expected, "mpcore.security", expectedCode);
        Assert.Empty(_host.Bus.Sent);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public Task No_token_is_401_and_never_reaches_the_bus(string endpoint) =>
        AssertRefused(endpoint, null, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");

    [Theory]
    [MemberData(nameof(Commands))]
    public Task A_fleet_manager_may_not_run_a_mission_command(string endpoint) =>
        // Decision 5: the mission commands are the operator's. A fleet manager reads them and nothing more.
        AssertRefused(endpoint, _host.Token(FleetManagerRole), HttpStatusCode.Forbidden, "FORBIDDEN");

    [Theory]
    [MemberData(nameof(Endpoints))]
    public Task An_administrator_may_not_reach_any_mission_endpoint(string endpoint) =>
        // AD-1: the administrator is not a superset role; it reads the audit trail only.
        AssertRefused(endpoint, _host.Token(AdministratorRole), HttpStatusCode.Forbidden, "FORBIDDEN");

    [Theory]
    [MemberData(nameof(Endpoints))]
    public Task A_valid_token_without_any_role_is_403(string endpoint) =>
        AssertRefused(endpoint, _host.Token(), HttpStatusCode.Forbidden, "FORBIDDEN");

    [Theory]
    [InlineData("create", OperatorRole, HttpStatusCode.Created, typeof(CreateMission))]
    [InlineData("schedule", OperatorRole, HttpStatusCode.OK, typeof(ScheduleMission))]
    [InlineData("assign", OperatorRole, HttpStatusCode.OK, typeof(AssignMission))]
    [InlineData("start", OperatorRole, HttpStatusCode.OK, typeof(StartMission))]
    [InlineData("complete", OperatorRole, HttpStatusCode.OK, typeof(CompleteMission))]
    [InlineData("cancel", OperatorRole, HttpStatusCode.OK, typeof(CancelMission))]
    [InlineData("get", OperatorRole, HttpStatusCode.OK, typeof(GetMission))]
    [InlineData("get", FleetManagerRole, HttpStatusCode.OK, typeof(GetMission))]
    [InlineData("active", OperatorRole, HttpStatusCode.OK, typeof(GetActiveMissions))]
    [InlineData("active", FleetManagerRole, HttpStatusCode.OK, typeof(GetActiveMissions))]
    public async Task The_right_role_reaches_the_command_or_query(
        string endpoint, string role, HttpStatusCode expected, Type message)
    {
        var response = await _host.Client(_host.Token(role)).SendAsync(MissionHttp.Request(endpoint));

        Assert.Equal(expected, response.StatusCode);
        Assert.IsType(message, Assert.Single(_host.Bus.Sent));
    }

    [Fact]
    public async Task Identity_headers_do_not_raise_a_fleet_manager_to_an_operator()
    {
        // CLAUDE.md: identity never comes from an arbitrary header.
        var request = MissionHttp.Request("assign");
        request.Headers.Add("X-Forwarded-User", "someone-else");
        request.Headers.Add("X-Forwarded-Roles", OperatorRole);

        var response = await _host.Client(_host.Token(FleetManagerRole)).SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_host.Bus.Sent);
    }
}

/// <summary>
/// The REST contract of the Operations module: what each endpoint sends to the bus, how the <c>:guid</c>
/// constraint keeps <c>{missionId}</c> from shadowing <c>/active</c>, and how each outcome reaches the
/// caller as a status and an RFC 9457 problem under the refusing module's own domain and code.
/// </summary>
public sealed class MissionEndpointContractTests : IClassFixture<FleetOperationsHostFactory>
{
    private readonly FleetOperationsHostFactory _host;
    private readonly HttpClient _operator;
    private readonly HttpClient _reader;

    public MissionEndpointContractTests(FleetOperationsHostFactory host)
    {
        _host = host;
        _host.Bus.Reset(static _ => throw new InvalidOperationException("Each test sets its response."));
        _operator = host.Client(host.Token(OperatorRole));
        _reader = host.Client(host.Token(FleetManagerRole));
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
    public async Task Create_is_201_with_the_location_of_the_new_mission_and_enum_names_in_the_body()
    {
        var id = Guid.CreateVersion7();
        _host.Bus.Reset(_ => Result<MissionView>.Success(
            new MissionView(id, "Tehran", "Isfahan", 800m, null, MissionStatus.Draft, null, null)));

        var response = await _operator.PostAsync(
            "/api/operations/missions",
            VehicleHttp.Json("{\"origin\":\"  Tehran  \",\"destination\":\"Isfahan\",\"requiredCapacityKg\":800.5}"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.EndsWith($"/api/operations/missions/{id}", response.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        // F-9: every REST body names enum members as strings, in requests and responses.
        Assert.Equal("Draft", body.RootElement.GetProperty("status").GetString());
        // O-1: a draft mission has no scheduled time, and the body says so with null rather than an epoch.
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("scheduledAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("assignedVehicleId").ValueKind);

        // The command carries what the caller sent, untrimmed, and nothing about the caller.
        var sent = Assert.IsType<CreateMission>(Assert.Single(_host.Bus.Sent));
        Assert.Equal("  Tehran  ", sent.Origin);
        Assert.Equal(800.5m, sent.RequiredCapacityKg);
    }

    [Fact]
    public async Task Create_without_a_body_field_sends_the_empty_value_for_the_validator_to_refuse()
    {
        _host.Bus.Reset(_ => Result<MissionView>.Success(MissionHttp.SampleView(Guid.CreateVersion7())));

        await _operator.PostAsync("/api/operations/missions", VehicleHttp.Json("{}"));

        var sent = Assert.IsType<CreateMission>(Assert.Single(_host.Bus.Sent));
        Assert.Equal(string.Empty, sent.Origin);
        Assert.Equal(string.Empty, sent.Destination);
        Assert.Equal(0m, sent.RequiredCapacityKg);
    }

    [Fact]
    public async Task Schedule_sends_the_route_id_and_the_body_time()
    {
        var id = Guid.CreateVersion7();
        _host.Bus.Reset(_ => Result<MissionView>.Success(MissionHttp.SampleView(id)));

        var response = await _operator.SendAsync(MissionHttp.Request("schedule", id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new ScheduleMission(id, MissionHttp.ScheduledAt), Assert.Single(_host.Bus.Sent));
    }

    [Fact]
    public async Task Assign_sends_the_route_id_and_both_body_ids()
    {
        var id = Guid.CreateVersion7();
        _host.Bus.Reset(_ => Result<MissionView>.Success(MissionHttp.SampleView(id, MissionStatus.Assigned)));

        var response = await _operator.SendAsync(MissionHttp.Request("assign", id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            new AssignMission(id, MissionHttp.VehicleId, MissionHttp.DriverId),
            Assert.Single(_host.Bus.Sent));
    }

    [Theory]
    [InlineData("start", MissionStatus.InProgress)]
    [InlineData("complete", MissionStatus.Completed)]
    [InlineData("cancel", MissionStatus.Cancelled)]
    public async Task A_data_less_transition_is_200_and_the_route_value_is_the_whole_input(string endpoint, MissionStatus status)
    {
        var id = Guid.CreateVersion7();
        _host.Bus.Reset(_ => Result<MissionView>.Success(MissionHttp.SampleView(id, status)));

        var response = await _operator.SendAsync(MissionHttp.Request(endpoint, id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(status.ToString(), body.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Cancel_sends_no_reason_because_there_is_none()
    {
        // O-10. The command record has one property, so nothing about a reason can travel even if a caller
        // sends one in a body the endpoint does not read.
        _host.Bus.Reset(_ => Result<MissionView>.Success(MissionHttp.SampleView(MissionHttp.MissionId, MissionStatus.Cancelled)));

        await _operator.SendAsync(MissionHttp.Request("cancel"));

        var sent = Assert.IsType<CancelMission>(Assert.Single(_host.Bus.Sent));
        Assert.Equal(MissionHttp.MissionId, sent.MissionId);
        Assert.Single(typeof(CancelMission).GetProperties());
    }

    [Theory]
    [InlineData("schedule")]
    [InlineData("assign")]
    [InlineData("start")]
    [InlineData("complete")]
    [InlineData("cancel")]
    [InlineData("get")]
    public async Task An_unknown_mission_is_404_under_the_modules_own_code(string endpoint)
    {
        _host.Bus.Reset(_ => Result<MissionView>.FromFailure(OperationsFailures.MissionNotFound()));
        var client = endpoint == "get" ? _reader : _operator;

        var response = await client.SendAsync(MissionHttp.Request(endpoint));

        await VehicleHttp.AssertProblem(response, HttpStatusCode.NotFound, "operations", "MISSION_NOT_FOUND");
        Assert.Equal(
            "The mission was not found.",
            (await VehicleHttp.ProblemOf(response)).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task An_unknown_vehicle_on_assign_is_404_under_the_Operations_code()
    {
        // AssignMission step 2. Operations answers with its own code because it may not reference Fleet's
        // failure descriptors; the narrow L-23 path below answers with Fleet's.
        _host.Bus.Reset(_ => Result<MissionView>.FromFailure(OperationsFailures.VehicleNotFound()));

        var response = await _operator.SendAsync(MissionHttp.Request("assign"));

        await VehicleHttp.AssertProblem(response, HttpStatusCode.NotFound, "operations", "MISSION_VEHICLE_NOT_FOUND");
    }

    [Fact]
    public async Task An_unknown_driver_on_assign_is_404_under_the_Operations_code()
    {
        _host.Bus.Reset(_ => Result<MissionView>.FromFailure(OperationsFailures.DriverNotFound()));

        var response = await _operator.SendAsync(MissionHttp.Request("assign"));

        await VehicleHttp.AssertProblem(response, HttpStatusCode.NotFound, "operations", "MISSION_DRIVER_NOT_FOUND");
    }

    [Fact]
    public async Task A_vehicle_that_disappeared_after_the_snapshot_is_404_under_Fleets_code_and_not_500()
    {
        // L-23 at the edge: the ResultFailureException the commitment port raises carries Fleet's own
        // descriptor, and MP Core maps it to the status that descriptor names.
        _host.Bus.Reset(static _ => throw new ResultFailureException(new FailureDescriptor(
            new ErrorIdentity("fleet", "VEHICLE_NOT_FOUND"),
            ErrorCategory.NotFound,
            new FailureMessageDescriptor("fleet.vehicle_not_found", null),
            null,
            null)));

        var response = await _operator.SendAsync(MissionHttp.Request("assign"));

        await VehicleHttp.AssertProblem(response, HttpStatusCode.NotFound, "fleet", "VEHICLE_NOT_FOUND");
    }

    [Fact]
    public async Task An_unassignable_mission_is_422_with_the_rule_code_and_its_text()
    {
        // Precondition 1. The text proves the Operations resource file is registered in the host's
        // message catalog.
        _host.Bus.Reset(_ => throw Thrown(() =>
            BusinessRules.Check(new MissionNotAssignableRule(MissionStatus.Draft))));

        var response = await _operator.SendAsync(MissionHttp.Request("assign"));

        await VehicleHttp.AssertProblem(response, HttpStatusCode.UnprocessableEntity, "operations", "MISSION_NOT_ASSIGNABLE");
        Assert.Equal(
            "Only a scheduled mission can be assigned.",
            (await VehicleHttp.ProblemOf(response)).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task An_illegal_transition_is_422_with_the_rule_code_and_its_text()
    {
        // O-3: cancelling an InProgress mission.
        _host.Bus.Reset(_ => throw Thrown(() =>
            BusinessRules.Check(new MissionInvalidTransitionRule(MissionStatus.InProgress, MissionTransition.Cancel))));

        var response = await _operator.SendAsync(MissionHttp.Request("cancel"));

        await VehicleHttp.AssertProblem(response, HttpStatusCode.UnprocessableEntity, "operations", "MISSION_INVALID_TRANSITION");
        Assert.Equal(
            "The mission cannot make this transition from its current status.",
            (await VehicleHttp.ProblemOf(response)).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task A_vehicle_under_maintenance_refuses_the_assignment_as_422_under_Fleets_own_domain()
    {
        // The canonical rejected-assignment case, seen from the edge: Operations orchestrates, but the
        // verdict and its vocabulary belong to Fleet.
        _host.Bus.Reset(_ => throw Thrown(() =>
            BusinessRules.Check(new VehicleUnderMaintenanceRule(MaintenanceStatus.UnderMaintenance))));

        var response = await _operator.SendAsync(MissionHttp.Request("assign"));

        await VehicleHttp.AssertProblem(response, HttpStatusCode.UnprocessableEntity, "fleet", "VEHICLE_UNDER_MAINTENANCE");
    }

    [Fact]
    public async Task A_lost_concurrency_race_on_a_mission_is_the_host_wide_409()
    {
        // L-1 and decision 2: two operators acting on one mission. The failure is host-wide because a lost
        // race can happen on any module's aggregate.
        _host.Bus.Reset(static _ => throw new DbUpdateConcurrencyException("The row was changed by another request."));

        var response = await _operator.SendAsync(MissionHttp.Request("start"));

        await VehicleHttp.AssertProblem(response, HttpStatusCode.Conflict, "fleetoperations", "CONCURRENCY_CONFLICT");
        Assert.DoesNotContain("The row was changed", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ux_missions_active_vehicle")]
    [InlineData("ux_missions_active_driver")]
    public async Task A_violation_of_either_partial_index_is_409_and_not_500(string index)
    {
        // O-9: the set-based half of decision 2 reaching a caller. Without the two new rows in
        // UniqueViolations.Known this would be the generic 500.
        _host.Bus.Reset(_ => throw new DbUpdateException(
            "save failed",
            new PostgresException(
                "duplicate key value violates unique constraint",
                "ERROR",
                "ERROR",
                PostgresErrorCodes.UniqueViolation,
                constraintName: index)));

        var response = await _operator.SendAsync(MissionHttp.Request("assign"));

        await VehicleHttp.AssertProblem(response, HttpStatusCode.Conflict, "fleetoperations", "CONCURRENCY_CONFLICT");
    }

    [Fact]
    public async Task Get_returns_the_view_with_ids_only()
    {
        var id = Guid.CreateVersion7();
        var vehicleId = Guid.CreateVersion7();
        var driverId = Guid.CreateVersion7();
        _host.Bus.Reset(_ => Result<MissionView>.Success(new MissionView(
            id, "Tehran", "Isfahan", 800m, MissionHttp.ScheduledAt, MissionStatus.Assigned, vehicleId, driverId)));

        var response = await _reader.SendAsync(MissionHttp.Request("get", id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(id, body.RootElement.GetProperty("id").GetGuid());
        Assert.Equal(vehicleId, body.RootElement.GetProperty("assignedVehicleId").GetGuid());
        Assert.Equal(driverId, body.RootElement.GetProperty("assignedDriverId").GetGuid());
        // Ids only: a client that needs the plate reads Fleet.
        Assert.False(body.RootElement.TryGetProperty("plateNumber", out _));
        Assert.False(body.RootElement.TryGetProperty("driverName", out _));
        Assert.Equal(new GetMission(id), Assert.Single(_host.Bus.Sent));
    }

    [Fact]
    public async Task The_active_route_reaches_GetActiveMissions_and_not_GetMission()
    {
        // The routing guarantee: /api/operations/missions/active and
        // /api/operations/missions/{missionId:guid} sit at the same depth, and the constraint is what sends
        // "active" to the literal route. Measured through the real matcher, not inferred from the pattern.
        var row = MissionHttp.SampleView(Guid.CreateVersion7());
        _host.Bus.Reset(_ => new Page<MissionView>([row], 1, 20, 1));

        var response = await _reader.SendAsync(MissionHttp.Request("active"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.IsType<GetActiveMissions>(Assert.Single(_host.Bus.Sent));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, body.RootElement.GetProperty("total").GetInt64());
        Assert.Equal("Tehran", Assert.Single(body.RootElement.GetProperty("items").EnumerateArray()).GetProperty("origin").GetString());
    }

    [Theory]
    [InlineData("?page=3&pageSize=10", 3, 10)]
    [InlineData("", 1, PageRequest.DefaultSize)]
    [InlineData("?page=0&pageSize=0", 1, PageRequest.DefaultSize)]
    public async Task The_page_query_string_becomes_a_normalised_PageRequest(string query, int number, int size)
    {
        _host.Bus.Reset(static message => new Page<MissionView>([], ((GetActiveMissions)message).Page.Number, ((GetActiveMissions)message).Page.Size, 0));

        var response = await _reader.GetAsync("/api/operations/missions/active" + query);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = Assert.IsType<GetActiveMissions>(Assert.Single(_host.Bus.Sent));
        Assert.Equal(number, sent.Page.Number);
        Assert.Equal(size, sent.Page.Size);
    }

    [Fact]
    public async Task An_unbounded_page_size_is_clamped_rather_than_refused()
    {
        _host.Bus.Reset(static message => new Page<MissionView>([], ((GetActiveMissions)message).Page.Number, ((GetActiveMissions)message).Page.Size, 0));

        await _reader.GetAsync("/api/operations/missions/active?page=1&pageSize=1000000");

        var sent = Assert.IsType<GetActiveMissions>(Assert.Single(_host.Bus.Sent));
        Assert.Equal(PageRequest.MaximumSize, sent.Page.Size);
    }

    [Fact]
    public async Task An_empty_active_page_is_200_with_an_empty_item_array_and_not_404()
    {
        _host.Bus.Reset(static _ => new Page<MissionView>([], 1, 20, 0));

        var response = await _reader.SendAsync(MissionHttp.Request("active"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Empty(body.RootElement.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task A_route_id_that_is_not_a_guid_matches_no_endpoint()
    {
        var response = await _reader.GetAsync("/api/operations/missions/not-a-guid");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(_host.Bus.Sent);
    }

    [Fact]
    public async Task A_transition_route_with_a_non_guid_id_matches_no_endpoint_either()
    {
        var response = await _operator.PostAsync("/api/operations/missions/not-a-guid/start", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(_host.Bus.Sent);
    }

    [Theory]
    [InlineData("{\"origin\":\"Tehran\",\"destination\":\"Isfahan\",\"requiredCapacityKg\":\"heavy\"}")]
    [InlineData("")]
    public async Task A_body_that_cannot_be_read_is_400_and_never_reaches_the_bus(string body)
    {
        // F-12: an unreadable body answers the host-wide MALFORMED_REQUEST problem rather than a bare 400.
        var response = await _operator.PostAsync("/api/operations/missions", VehicleHttp.Json(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_host.Bus.Sent);
        await VehicleHttp.AssertProblem(response, HttpStatusCode.BadRequest, "mpcore.http", "MALFORMED_REQUEST");
    }
}
