using Fserp.FleetOperations.Modules.Operations.Application;
using Fserp.FleetOperations.Modules.Operations.Application.Commands;
using Fserp.FleetOperations.Modules.Operations.Application.Queries;
using Fserp.FleetOperations.Modules.Operations.Application.Views;
using Fserp.FleetOperations.Modules.Operations.Domain;
using MPCore.Application.Querying;
using MPCore.Application.Results;
using MPCore.Audit;
using MPCore.Domain.Rules;

namespace Fserp.FleetOperations.UnitTests.Operations;

/// <summary>
/// The five command handlers other than <c>AssignMission</c>, against in-memory fakes of their ports:
/// what each returns for an unknown mission, what each refuses, what each audits, and — for Complete and
/// Cancel — which release ports it calls and which it does not.
/// </summary>
public sealed class MissionHandlerTests
{
    private readonly PortCallLog _log = new();
    private readonly FakeMissionRepository _missions;
    private readonly FakeVehicleCommitments _vehicles;
    private readonly FakeDriverCommitments _drivers;
    private readonly LoggingAuditRecorder _audit;
    private readonly FakeOperationsUnitOfWork _unitOfWork = new();
    private readonly FixedOperationsClock _clock = new(TestMissions.Now);

    public MissionHandlerTests()
    {
        _missions = new FakeMissionRepository(_log);
        _vehicles = new FakeVehicleCommitments(_log);
        _drivers = new FakeDriverCommitments(_log);
        _audit = new LoggingAuditRecorder(_log);
    }

    private Task<Result<MissionView>> Create(string origin = "Tehran", string destination = "Isfahan", decimal kg = 800m) =>
        CreateMissionHandler.Handle(
            new CreateMission(origin, destination, kg), _missions, _unitOfWork, _clock, _audit, CancellationToken.None);

    private Task<Result<MissionView>> Schedule(Guid missionId, DateTimeOffset? at = null) =>
        ScheduleMissionHandler.Handle(
            new ScheduleMission(missionId, at ?? TestMissions.ScheduledAt),
            _missions, _unitOfWork, _audit, CancellationToken.None);

    private Task<Result<MissionView>> Start(Guid missionId) =>
        StartMissionHandler.Handle(new StartMission(missionId), _missions, _unitOfWork, _audit, CancellationToken.None);

    private Task<Result<MissionView>> Complete(Guid missionId) =>
        CompleteMissionHandler.Handle(
            new CompleteMission(missionId), _missions, _unitOfWork, _vehicles, _drivers, _audit, CancellationToken.None);

    private Task<Result<MissionView>> Cancel(Guid missionId) =>
        CancelMissionHandler.Handle(
            new CancelMission(missionId), _missions, _unitOfWork, _vehicles, _drivers, _audit, CancellationToken.None);

    // ---- CreateMission ----

    [Fact]
    public async Task Create_adds_a_draft_mission_and_records_the_action()
    {
        var result = await Create();

        Assert.True(result.IsSuccess);
        var added = Assert.Single(_missions.Added);
        Assert.Equal(MissionStatus.Draft, added.Status);
        Assert.Null(result.Value.ScheduledAt);
        Assert.Equal(MissionStatus.Draft, result.Value.Status);
        var recorded = Assert.Single(_audit.Actions);
        Assert.Equal("Operations", recorded.Module);
        Assert.Equal("MissionCreated", recorded.Action);
        Assert.Equal("Mission", recorded.EntityType);
        Assert.Equal(added.Id.ToString(), recorded.EntityId);
    }

    [Fact]
    public async Task Create_takes_the_identitys_timestamp_from_the_clock_port()
    {
        var result = await Create();

        // A version 7 UUID built from the injected clock, not from DateTime.UtcNow inside the handler.
        Assert.Equal(7, result.Value.Id.Version);
    }

    [Theory]
    [InlineData("", "Isfahan", "MISSION_LOCATION_REQUIRED")]
    [InlineData("   ", "Isfahan", "MISSION_LOCATION_REQUIRED")]
    [InlineData("Tehran", "", "MISSION_LOCATION_REQUIRED")]
    public async Task Create_refuses_an_empty_location_before_anything_is_tracked(string origin, string destination, string code)
    {
        var refused = await Assert.ThrowsAsync<BusinessRuleValidationException>(() => Create(origin, destination));

        Assert.Equal(code, refused.Rule.Code);
        Assert.Empty(_missions.Added);
        Assert.Empty(_audit.Actions);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Create_refuses_a_non_positive_capacity_before_anything_is_tracked(decimal kg)
    {
        var refused = await Assert.ThrowsAsync<BusinessRuleValidationException>(() => Create(kg: kg));

        Assert.Equal("MISSION_REQUIRED_CAPACITY_MUST_BE_POSITIVE", refused.Rule.Code);
        Assert.Empty(_missions.Added);
        Assert.Empty(_audit.Actions);
    }

    [Fact]
    public async Task Create_accepts_an_origin_equal_to_the_destination()
    {
        var result = await Create("Tehran", "Tehran");

        Assert.True(result.IsSuccess);
        Assert.Equal("Tehran", result.Value.Origin);
        Assert.Equal("Tehran", result.Value.Destination);
    }

    // ---- ScheduleMission ----

    [Fact]
    public async Task Schedule_sets_the_time_and_records_the_transition()
    {
        var mission = _missions.Store(TestMissions.Draft());

        var result = await Schedule(mission.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(TestMissions.ScheduledAt, result.Value.ScheduledAt);
        Assert.Equal(MissionStatus.Scheduled, result.Value.Status);
        var recorded = Assert.Single(_audit.Actions);
        Assert.Equal("MissionScheduled", recorded.Action);
        Assert.Equal("Draft", recorded.Metadata!["from"]);
        Assert.Equal("Scheduled", recorded.Metadata["to"]);
    }

    [Fact]
    public async Task Schedule_accepts_a_time_in_the_past()
    {
        // O-2 through the handler: no validator and no rule compares the time with a clock.
        var mission = _missions.Store(TestMissions.Draft());
        var longAgo = new DateTimeOffset(2001, 2, 3, 4, 5, 6, TimeSpan.Zero);

        var result = await Schedule(mission.Id, longAgo);

        Assert.Equal(longAgo, result.Value.ScheduledAt);
    }

    [Fact]
    public async Task Schedule_of_an_unknown_mission_is_404_and_records_nothing()
    {
        var result = await Schedule(Guid.CreateVersion7());

        Assert.True(result.IsFailure);
        Assert.Equal(new ErrorIdentity("operations", "MISSION_NOT_FOUND"), result.FailureDescriptor!.Identity);
        Assert.Equal(ErrorCategory.NotFound, result.FailureDescriptor.Category);
        Assert.Equal("operations.mission_not_found", result.FailureDescriptor.Message.Key);
        Assert.Empty(_audit.Actions);
        Assert.Empty(_audit.Attempts);
    }

    [Fact]
    public async Task Scheduling_an_already_scheduled_mission_is_refused_and_the_attempt_is_audited()
    {
        var mission = _missions.Store(TestMissions.Scheduled());

        var refused = await Assert.ThrowsAsync<BusinessRuleValidationException>(() => Schedule(mission.Id));

        Assert.Equal("MISSION_INVALID_TRANSITION", refused.Rule.Code);
        var attempt = Assert.Single(_audit.Attempts);
        Assert.Equal(AuditOutcome.Rejected, attempt.Outcome);
        Assert.Equal("MissionScheduled", attempt.Action);
        Assert.Equal(new AuditFailure("operations", "MISSION_INVALID_TRANSITION"), attempt.Failure);
        Assert.Equal("Scheduled", attempt.Metadata!["from"]);
        Assert.Empty(_audit.Actions);
    }

    // ---- StartMission ----

    [Fact]
    public async Task Start_moves_the_mission_and_touches_neither_Fleet_nor_Drivers()
    {
        var mission = _missions.Store(TestMissions.Assigned(Guid.CreateVersion7(), Guid.CreateVersion7()));

        var result = await Start(mission.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(MissionStatus.InProgress, result.Value.Status);
        // docs/plans/operations.md: "Start changes nothing in Fleet or Drivers." The handler does not even
        // declare the two commitment ports, which the shape test below pins.
        Assert.Empty(_vehicles.Releases);
        Assert.Empty(_drivers.Releases);
        Assert.Equal("MissionStarted", Assert.Single(_audit.Actions).Action);
    }

    [Fact]
    public void Start_declares_neither_commitment_port()
    {
        var ports = typeof(StartMissionHandler)
            .GetMethod(nameof(StartMissionHandler.Handle))!
            .GetParameters()
            .Select(parameter => parameter.ParameterType.Name)
            .ToList();

        Assert.DoesNotContain("IVehicleCommitments", ports);
        Assert.DoesNotContain("IDriverCommitments", ports);
    }

    [Fact]
    public async Task Start_of_an_unknown_mission_is_404()
    {
        var result = await Start(Guid.CreateVersion7());

        Assert.True(result.IsFailure);
        Assert.Equal("MISSION_NOT_FOUND", result.FailureDescriptor!.Identity.Code);
    }

    [Theory]
    [InlineData(MissionStatus.Draft)]
    [InlineData(MissionStatus.Scheduled)]
    [InlineData(MissionStatus.InProgress)]
    [InlineData(MissionStatus.Completed)]
    [InlineData(MissionStatus.Cancelled)]
    public async Task Start_from_any_other_status_is_refused_and_audited(MissionStatus status)
    {
        var mission = _missions.Store(TestMissions.In(status));

        var refused = await Assert.ThrowsAsync<BusinessRuleValidationException>(() => Start(mission.Id));

        Assert.Equal("MISSION_INVALID_TRANSITION", refused.Rule.Code);
        Assert.Equal(status.ToString(), Assert.Single(_audit.Attempts).Metadata!["from"]);
    }

    // ---- CompleteMission ----

    [Fact]
    public async Task Complete_releases_both_resources_in_its_own_transaction()
    {
        var vehicleId = Guid.CreateVersion7();
        var driverId = Guid.CreateVersion7();
        var mission = _missions.Store(TestMissions.InProgress(vehicleId, driverId));

        var result = await Complete(mission.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(MissionStatus.Completed, result.Value.Status);
        Assert.Equal((vehicleId, mission.Id), Assert.Single(_vehicles.Releases));
        Assert.Equal((driverId, mission.Id), Assert.Single(_drivers.Releases));
        // The handler never saves: the middleware commits the mission row, the vehicle row, the driver row
        // and the audit rows together.
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task Complete_checks_the_transition_before_it_touches_either_resource()
    {
        var vehicleId = Guid.CreateVersion7();
        var driverId = Guid.CreateVersion7();
        var mission = _missions.Store(TestMissions.InProgress(vehicleId, driverId));

        await Complete(mission.Id);

        Assert.Equal(
            [
                FakeMissionRepository.LoadCall,
                FakeVehicleCommitments.ReleaseCall,
                FakeDriverCommitments.ReleaseCall,
                LoggingAuditRecorder.RecordCall,
            ],
            _log.Calls);
    }

    [Theory]
    [InlineData(MissionStatus.Draft)]
    [InlineData(MissionStatus.Scheduled)]
    [InlineData(MissionStatus.Assigned)]
    [InlineData(MissionStatus.Completed)]
    [InlineData(MissionStatus.Cancelled)]
    public async Task A_refused_Complete_releases_nothing(MissionStatus status)
    {
        var mission = _missions.Store(TestMissions.In(status));

        var refused = await Assert.ThrowsAsync<BusinessRuleValidationException>(() => Complete(mission.Id));

        Assert.Equal("MISSION_INVALID_TRANSITION", refused.Rule.Code);
        Assert.Empty(_vehicles.Releases);
        Assert.Empty(_drivers.Releases);
        Assert.Equal("MissionCompleted", Assert.Single(_audit.Attempts).Action);
        Assert.Empty(_audit.Actions);
    }

    [Fact]
    public async Task Complete_of_an_unknown_mission_is_404()
    {
        var result = await Complete(Guid.CreateVersion7());

        Assert.True(result.IsFailure);
        Assert.Equal("MISSION_NOT_FOUND", result.FailureDescriptor!.Identity.Code);
        Assert.Empty(_vehicles.Releases);
    }

    // ---- CancelMission ----

    [Theory]
    [InlineData(MissionStatus.Draft)]
    [InlineData(MissionStatus.Scheduled)]
    public async Task Cancel_from_Draft_or_Scheduled_releases_nothing(MissionStatus status)
    {
        // L-24: nothing was committed, and releasing a resource that was never committed would itself be
        // refused with VEHICLE_NOT_COMMITTED_TO_MISSION.
        var mission = _missions.Store(TestMissions.In(status));

        var result = await Cancel(mission.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(MissionStatus.Cancelled, result.Value.Status);
        Assert.Empty(_vehicles.Releases);
        Assert.Empty(_drivers.Releases);
        var recorded = Assert.Single(_audit.Actions);
        Assert.Equal("MissionCancelled", recorded.Action);
        Assert.Equal(status.ToString(), recorded.Metadata!["from"]);
        Assert.Equal("Cancelled", recorded.Metadata["to"]);
    }

    [Fact]
    public async Task Cancel_from_Assigned_releases_both_resources()
    {
        var vehicleId = Guid.CreateVersion7();
        var driverId = Guid.CreateVersion7();
        var mission = _missions.Store(TestMissions.Assigned(vehicleId, driverId));

        var result = await Cancel(mission.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal((vehicleId, mission.Id), Assert.Single(_vehicles.Releases));
        Assert.Equal((driverId, mission.Id), Assert.Single(_drivers.Releases));
    }

    [Theory]
    [InlineData(MissionStatus.InProgress)]
    [InlineData(MissionStatus.Completed)]
    [InlineData(MissionStatus.Cancelled)]
    public async Task Cancel_from_a_status_the_plan_refuses_is_422_and_releases_nothing(MissionStatus status)
    {
        // O-3 for InProgress, terminality for the other two.
        var mission = _missions.Store(TestMissions.In(status));

        var refused = await Assert.ThrowsAsync<BusinessRuleValidationException>(() => Cancel(mission.Id));

        Assert.Equal("MISSION_INVALID_TRANSITION", refused.Rule.Code);
        Assert.Empty(_vehicles.Releases);
        Assert.Empty(_drivers.Releases);
        Assert.Equal("MissionCancelled", Assert.Single(_audit.Attempts).Action);
    }

    [Fact]
    public async Task Cancel_of_an_unknown_mission_is_404()
    {
        var result = await Cancel(Guid.CreateVersion7());

        Assert.True(result.IsFailure);
        Assert.Equal("MISSION_NOT_FOUND", result.FailureDescriptor!.Identity.Code);
        Assert.Empty(_audit.Attempts);
    }

    [Fact]
    public async Task No_command_handler_saves_its_own_work()
    {
        var mission = _missions.Store(TestMissions.Draft());
        await Create();
        await Schedule(mission.Id);

        Assert.Equal(0, _unitOfWork.Saves);
    }
}

/// <summary>The two queries: they read, and that is all they do.</summary>
public sealed class MissionQueryTests
{
    private readonly FakeMissionReadModel _missions = new();

    private static MissionView View(Guid id, MissionStatus status = MissionStatus.Scheduled) =>
        new(id, "Tehran", "Isfahan", 800m, TestMissions.ScheduledAt, status, null, null);

    [Fact]
    public async Task GetMission_returns_the_view_from_the_read_model()
    {
        var id = Guid.CreateVersion7();
        _missions.View = View(id);

        var result = await GetMissionHandler.Handle(new GetMission(id), _missions, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(id, result.Value.Id);
        Assert.Equal(id, _missions.AskedFor);
        Assert.Equal(1, _missions.Reads);
    }

    [Fact]
    public async Task GetMission_of_an_unknown_mission_is_404()
    {
        _missions.View = null;

        var result = await GetMissionHandler.Handle(
            new GetMission(Guid.CreateVersion7()), _missions, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(new ErrorIdentity("operations", "MISSION_NOT_FOUND"), result.FailureDescriptor!.Identity);
        Assert.Equal(ErrorCategory.NotFound, result.FailureDescriptor.Category);
    }

    [Fact]
    public async Task GetActiveMissions_returns_the_requested_page_and_the_total()
    {
        for (var index = 0; index < 25; index++)
        {
            _missions.Active.Add(View(Guid.CreateVersion7()));
        }

        var page = await GetActiveMissionsHandler.Handle(
            new GetActiveMissions(new PageRequest(3, 10)), _missions, CancellationToken.None);

        Assert.Equal(3, page.Number);
        Assert.Equal(10, page.Size);
        Assert.Equal(25, page.Total);
        Assert.Equal(5, page.Items.Count);
        Assert.Equal(new PageRequest(3, 10), _missions.AskedForPage);
    }

    [Fact]
    public async Task An_empty_page_is_a_page_and_never_a_404()
    {
        var page = await GetActiveMissionsHandler.Handle(
            new GetActiveMissions(PageRequest.First), _missions, CancellationToken.None);

        Assert.Empty(page.Items);
        Assert.Equal(0, page.Total);
    }

    [Fact]
    public async Task The_page_request_normalises_itself_so_the_query_is_always_bounded()
    {
        // PageRequest clamps the number to at least one and the size to at most MaximumSize, so a caller
        // asking for page 0 or a million rows gets a bounded page rather than an outage. The query carries
        // the normalised value, which is what the read model receives.
        var page = await GetActiveMissionsHandler.Handle(
            new GetActiveMissions(new PageRequest(0, 1_000_000)), _missions, CancellationToken.None);

        Assert.Equal(1, page.Number);
        Assert.Equal(PageRequest.MaximumSize, page.Size);
        Assert.True(page.Size < 1_000_000);
    }
}
