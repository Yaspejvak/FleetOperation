using Fserp.FleetOperations.Modules.Operations.Application;
using Fserp.FleetOperations.Modules.Operations.Application.Queries;
using Fserp.FleetOperations.Modules.Operations.Application.Views;
using Fserp.FleetOperations.Modules.Operations.Domain;
using Fserp.FleetOperations.Modules.Operations.Domain.Rules;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using MPCore.Application.Querying;
using MPCore.Application.Results;
using MPCore.Domain.Rules;
using Npgsql;
using static Fserp.FleetOperations.UnitTests.Host.FleetOperationsHostFactory;
using Proto = Fserp.FleetOperations.Api.Grpc.Operations.V1;

namespace Fserp.FleetOperations.UnitTests.Host;

/// <summary>
/// The gRPC Operations read surface through the real host pipeline (docs/architecture.md, "Transport
/// parity"). A real gRPC client speaks to the in-memory server, so every status below is one a caller
/// observes rather than one read off the mapping code.
/// </summary>
public sealed class MissionGrpcServiceTests : IClassFixture<FleetOperationsHostFactory>, IDisposable
{
    private static readonly DateTimeOffset ScheduledAt = new(2026, 10, 6, 7, 30, 0, TimeSpan.Zero);

    private readonly FleetOperationsHostFactory _host;
    private readonly List<GrpcChannel> _channels = [];

    public MissionGrpcServiceTests(FleetOperationsHostFactory host)
    {
        _host = host;
        _host.Bus.Reset(static _ => throw new InvalidOperationException("Each test sets its response."));
    }

    public void Dispose()
    {
        foreach (var channel in _channels)
        {
            channel.Dispose();
        }
    }

    private Proto.MissionService.MissionServiceClient Client()
    {
        var handler = _host.Server.CreateHandler();
        var channel = GrpcChannel.ForAddress(
            _host.Server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = handler, Credentials = ChannelCredentials.Insecure });
        _channels.Add(channel);
        return new Proto.MissionService.MissionServiceClient(channel);
    }

    private static Metadata Bearer(string token) => new() { { "Authorization", "Bearer " + token } };

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

    private static MissionView View(
        Guid id,
        MissionStatus status = MissionStatus.Scheduled,
        DateTimeOffset? scheduledAt = null,
        Guid? vehicleId = null,
        Guid? driverId = null) =>
        new(id, "Tehran", "Isfahan", 800.25m, scheduledAt ?? ScheduledAt, status, vehicleId, driverId);

    [Fact]
    public async Task GetMission_sends_the_same_query_record_REST_sends_and_maps_the_view()
    {
        var id = Guid.CreateVersion7();
        _host.Bus.Reset(_ => Result<MissionView>.Success(View(id)));

        var response = await Client().GetMissionAsync(
            new Proto.GetMissionRequest { MissionId = id.ToString() },
            Bearer(_host.Token(OperatorRole)));

        // The very same Application query the REST endpoint builds: one use case, two adapters.
        Assert.Equal(new GetMission(id), Assert.Single(_host.Bus.Sent));
        Assert.Equal(id.ToString(), response.Mission.MissionId);
        Assert.Equal("Tehran", response.Mission.Origin);
        Assert.Equal("Isfahan", response.Mission.Destination);
        // Decimal in invariant culture, because protobuf has no decimal and a double would change it.
        Assert.Equal("800.25", response.Mission.RequiredCapacityKg);
        Assert.Equal(Proto.MissionStatus.Scheduled, response.Mission.Status);
        Assert.Equal(ScheduledAt, response.Mission.ScheduledAt.ToDateTimeOffset());
        // The two assigned ids are optional and absent until the mission is assigned.
        Assert.False(response.Mission.HasAssignedVehicleId);
        Assert.False(response.Mission.HasAssignedDriverId);
    }

    [Fact]
    public async Task A_draft_mission_carries_no_scheduled_at_at_all()
    {
        // O-1: absent, not an epoch zero. A message field is null until it is set, which is what the proto
        // comment promises.
        _host.Bus.Reset(_ => Result<MissionView>.Success(
            new MissionView(Guid.CreateVersion7(), "Tehran", "Isfahan", 10m, null, MissionStatus.Draft, null, null)));

        var response = await Client().GetMissionAsync(
            new Proto.GetMissionRequest { MissionId = Guid.CreateVersion7().ToString() },
            Bearer(_host.Token(OperatorRole)));

        Assert.Null(response.Mission.ScheduledAt);
        Assert.Equal(Proto.MissionStatus.Draft, response.Mission.Status);
    }

    [Fact]
    public async Task An_assigned_mission_carries_both_optional_ids()
    {
        var vehicleId = Guid.CreateVersion7();
        var driverId = Guid.CreateVersion7();
        _host.Bus.Reset(_ => Result<MissionView>.Success(
            View(Guid.CreateVersion7(), MissionStatus.Assigned, vehicleId: vehicleId, driverId: driverId)));

        var response = await Client().GetMissionAsync(
            new Proto.GetMissionRequest { MissionId = Guid.CreateVersion7().ToString() },
            Bearer(_host.Token(OperatorRole)));

        Assert.True(response.Mission.HasAssignedVehicleId);
        Assert.Equal(vehicleId.ToString(), response.Mission.AssignedVehicleId);
        Assert.True(response.Mission.HasAssignedDriverId);
        Assert.Equal(driverId.ToString(), response.Mission.AssignedDriverId);
    }

    [Theory]
    [InlineData(MissionStatus.Draft, Proto.MissionStatus.Draft)]
    [InlineData(MissionStatus.Scheduled, Proto.MissionStatus.Scheduled)]
    [InlineData(MissionStatus.Assigned, Proto.MissionStatus.Assigned)]
    [InlineData(MissionStatus.InProgress, Proto.MissionStatus.InProgress)]
    [InlineData(MissionStatus.Completed, Proto.MissionStatus.Completed)]
    [InlineData(MissionStatus.Cancelled, Proto.MissionStatus.Cancelled)]
    public async Task Every_one_of_the_six_states_maps_to_its_own_proto_member(
        MissionStatus status, Proto.MissionStatus expected)
    {
        // No state falls through to UNSPECIFIED, which would silently tell a client "I do not know".
        _host.Bus.Reset(_ => Result<MissionView>.Success(View(Guid.CreateVersion7(), status)));

        var response = await Client().GetMissionAsync(
            new Proto.GetMissionRequest { MissionId = Guid.CreateVersion7().ToString() },
            Bearer(_host.Token(OperatorRole)));

        Assert.Equal(expected, response.Mission.Status);
        Assert.NotEqual(Proto.MissionStatus.Unspecified, response.Mission.Status);
    }

    [Fact]
    public async Task An_unknown_mission_is_NotFound_under_the_same_failure_REST_renders()
    {
        // The parity that matters: one FailureDescriptor, two transports.
        _host.Bus.Reset(static _ => Result<MissionView>.FromFailure(OperationsFailures.MissionNotFound()));

        var refused = await Assert.ThrowsAsync<RpcException>(() => Client().GetMissionAsync(
            new Proto.GetMissionRequest { MissionId = Guid.CreateVersion7().ToString() },
            Bearer(_host.Token(OperatorRole))).ResponseAsync);

        Assert.Equal(StatusCode.NotFound, refused.StatusCode);
        Assert.Equal(ErrorCategory.NotFound, OperationsFailures.MissionNotFound().Category);
        Assert.Equal("MISSION_NOT_FOUND", OperationsFailures.MissionNotFound().Identity.Code);
    }

    [Fact]
    public async Task A_mission_id_that_is_not_a_uuid_is_a_validation_failure_and_not_a_fabricated_code()
    {
        // L-7: Guid.TryParse yields Guid.Empty, which GetMissionValidator's NotEmpty() refuses. The host
        // bus is a stand-in here, so the record reaching it is what the assertion pins.
        _host.Bus.Reset(static _ => Result<MissionView>.FromFailure(
            new FailureDescriptor(
                new ErrorIdentity("mpcore.validation", "VALIDATION_FAILED"),
                ErrorCategory.Validation,
                new FailureMessageDescriptor("mpcore.validation.failed", null),
                null,
                null)));

        var refused = await Assert.ThrowsAsync<RpcException>(() => Client().GetMissionAsync(
            new Proto.GetMissionRequest { MissionId = "not-a-uuid" },
            Bearer(_host.Token(OperatorRole))).ResponseAsync);

        Assert.Equal(StatusCode.InvalidArgument, refused.StatusCode);
        Assert.Equal(new GetMission(Guid.Empty), Assert.Single(_host.Bus.Sent));
    }

    [Fact]
    public async Task A_broken_rule_is_FailedPrecondition_under_the_rules_own_code()
    {
        _host.Bus.Reset(_ => throw Thrown(() =>
            BusinessRules.Check(new MissionInvalidTransitionRule(MissionStatus.Completed, MissionTransition.Start))));

        var refused = await Assert.ThrowsAsync<RpcException>(() => Client().GetMissionAsync(
            new Proto.GetMissionRequest { MissionId = Guid.CreateVersion7().ToString() },
            Bearer(_host.Token(OperatorRole))).ResponseAsync);

        Assert.Equal(StatusCode.FailedPrecondition, refused.StatusCode);
    }

    [Fact]
    public async Task GetActiveMissions_sends_the_same_query_record_REST_sends_and_maps_every_row()
    {
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        _host.Bus.Reset(_ => new Page<MissionView>(
            [View(first, MissionStatus.Scheduled), View(second, MissionStatus.InProgress)], 2, 10, 42));

        var response = await Client().GetActiveMissionsAsync(
            new Proto.GetActiveMissionsRequest { Page = 2, PageSize = 10 },
            Bearer(_host.Token(FleetManagerRole)));

        var sent = Assert.IsType<GetActiveMissions>(Assert.Single(_host.Bus.Sent));
        Assert.Equal(2, sent.Page.Number);
        Assert.Equal(10, sent.Page.Size);
        Assert.Equal(2, response.Missions.Count);
        Assert.Equal(2, response.Page);
        Assert.Equal(10, response.PageSize);
        Assert.Equal(42, response.Total);
        Assert.Equal(first.ToString(), response.Missions[0].MissionId);
        Assert.Equal(Proto.MissionStatus.InProgress, response.Missions[1].Status);
    }

    [Fact]
    public async Task An_absent_page_means_the_first_page_at_the_default_size()
    {
        // The proto comment's promise: "Zero or absent means the first page; the server normalises and
        // bounds both values."
        _host.Bus.Reset(static message =>
        {
            var query = (GetActiveMissions)message;
            return new Page<MissionView>([], query.Page.Number, query.Page.Size, 0);
        });

        var response = await Client().GetActiveMissionsAsync(
            new Proto.GetActiveMissionsRequest(),
            Bearer(_host.Token(OperatorRole)));

        var sent = Assert.IsType<GetActiveMissions>(Assert.Single(_host.Bus.Sent));
        Assert.Equal(1, sent.Page.Number);
        Assert.Equal(PageRequest.DefaultSize, sent.Page.Size);
        Assert.Equal(1, response.Page);
    }

    [Fact]
    public async Task An_unbounded_page_size_is_clamped_over_gRPC_too()
    {
        _host.Bus.Reset(static message =>
        {
            var query = (GetActiveMissions)message;
            return new Page<MissionView>([], query.Page.Number, query.Page.Size, 0);
        });

        await Client().GetActiveMissionsAsync(
            new Proto.GetActiveMissionsRequest { Page = 1, PageSize = 1_000_000 },
            Bearer(_host.Token(OperatorRole)));

        Assert.Equal(PageRequest.MaximumSize, Assert.IsType<GetActiveMissions>(Assert.Single(_host.Bus.Sent)).Page.Size);
    }

    [Fact]
    public async Task An_empty_page_is_an_empty_repeated_field()
    {
        _host.Bus.Reset(static _ => new Page<MissionView>([], 1, 20, 0));

        var response = await Client().GetActiveMissionsAsync(
            new Proto.GetActiveMissionsRequest(),
            Bearer(_host.Token(OperatorRole)));

        Assert.Empty(response.Missions);
        Assert.Equal(0, response.Total);
    }

    [Fact]
    public async Task No_token_is_Unauthenticated_and_never_reaches_the_bus()
    {
        _host.Bus.Reset(static _ => Result<MissionView>.Success(View(Guid.CreateVersion7())));

        var refused = await Assert.ThrowsAsync<RpcException>(() => Client().GetMissionAsync(
            new Proto.GetMissionRequest { MissionId = Guid.CreateVersion7().ToString() }).ResponseAsync);

        Assert.Equal(StatusCode.Unauthenticated, refused.StatusCode);
        Assert.Empty(_host.Bus.Sent);
    }

    [Theory]
    [InlineData(AdministratorRole)]
    [InlineData(null)]
    public async Task A_caller_without_a_reader_role_is_PermissionDenied_and_never_reaches_the_bus(string? role)
    {
        // Decision 5: the gRPC reads carry the same OperationalReader policy as the REST reads.
        // AD-1: the administrator is not a superset role.
        _host.Bus.Reset(static _ => Result<MissionView>.Success(View(Guid.CreateVersion7())));
        var token = role is null ? _host.Token() : _host.Token(role);

        var refused = await Assert.ThrowsAsync<RpcException>(() => Client().GetMissionAsync(
            new Proto.GetMissionRequest { MissionId = Guid.CreateVersion7().ToString() },
            Bearer(token)).ResponseAsync);

        Assert.Equal(StatusCode.PermissionDenied, refused.StatusCode);
        Assert.Empty(_host.Bus.Sent);
    }

    [Fact]
    public async Task The_active_list_over_gRPC_carries_the_same_policy()
    {
        _host.Bus.Reset(static _ => new Page<MissionView>([], 1, 20, 0));

        var refused = await Assert.ThrowsAsync<RpcException>(() => Client().GetActiveMissionsAsync(
            new Proto.GetActiveMissionsRequest(),
            Bearer(_host.Token(AdministratorRole))).ResponseAsync);

        Assert.Equal(StatusCode.PermissionDenied, refused.StatusCode);
        Assert.Empty(_host.Bus.Sent);
    }

    [Fact]
    public async Task A_lost_concurrency_race_answers_Aborted_over_gRPC()
    {
        // O-9 on the gRPC side: ErrorCategory.Concurrency maps to ABORTED, from the same mapper the HTTP
        // side uses.
        _host.Bus.Reset(static _ => throw new DbUpdateConcurrencyException("The row was changed."));

        var refused = await Assert.ThrowsAsync<RpcException>(() => Client().GetMissionAsync(
            new Proto.GetMissionRequest { MissionId = Guid.CreateVersion7().ToString() },
            Bearer(_host.Token(OperatorRole))).ResponseAsync);

        Assert.Equal(StatusCode.Aborted, refused.StatusCode);
    }

    [Theory]
    [InlineData("ux_missions_active_vehicle")]
    [InlineData("ux_missions_active_driver")]
    public async Task A_violation_of_either_partial_index_answers_Aborted_over_gRPC_too(string index)
    {
        // The two new rows of UniqueViolations.Known reach gRPC through the same table, so the two
        // transports cannot disagree about what a lost assignment race is.
        _host.Bus.Reset(_ => throw new DbUpdateException(
            "save failed",
            new PostgresException(
                "duplicate key value violates unique constraint",
                "ERROR",
                "ERROR",
                PostgresErrorCodes.UniqueViolation,
                constraintName: index)));

        var refused = await Assert.ThrowsAsync<RpcException>(() => Client().GetMissionAsync(
            new Proto.GetMissionRequest { MissionId = Guid.CreateVersion7().ToString() },
            Bearer(_host.Token(OperatorRole))).ResponseAsync);

        Assert.Equal(StatusCode.Aborted, refused.StatusCode);
    }
}
