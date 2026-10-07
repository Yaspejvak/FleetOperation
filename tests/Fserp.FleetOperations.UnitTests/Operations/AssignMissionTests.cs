using Fserp.FleetOperations.Modules.Drivers.Domain;
using Fserp.FleetOperations.Modules.Drivers.Domain.Rules;
using Fserp.FleetOperations.Modules.Fleet.Contracts;
using Fserp.FleetOperations.Modules.Fleet.Domain;
using Fserp.FleetOperations.Modules.Fleet.Domain.Rules;
using Fserp.FleetOperations.Modules.Operations.Application;
using Fserp.FleetOperations.Modules.Operations.Application.Commands;
using Fserp.FleetOperations.Modules.Operations.Domain;
using Fserp.FleetOperations.Modules.Operations.Domain.Rules;
using MPCore.Application.Results;
using MPCore.Audit;
using MPCore.Domain.Rules;

namespace Fserp.FleetOperations.UnitTests.Operations;

/// <summary>
/// <c>AssignMission</c>, the one handler whose step order is a correctness property
/// (docs/plans/operations.md, "AssignMission: handler shape and transaction boundary"; decision 2).
/// </summary>
public sealed class AssignMissionTests
{
    private readonly PortCallLog _log = new();
    private readonly FakeMissionRepository _missions;
    private readonly FakeVehicleAvailabilityReader _vehicleReader;
    private readonly FakeDriverEligibilityReader _driverReader;
    private readonly FakeVehicleCommitments _vehicles;
    private readonly FakeDriverCommitments _drivers;
    private readonly LoggingAuditRecorder _audit;
    private readonly FakeOperationsUnitOfWork _unitOfWork = new();

    private readonly Guid _vehicleId = Guid.CreateVersion7();
    private readonly Guid _driverId = Guid.CreateVersion7();

    public AssignMissionTests()
    {
        _missions = new FakeMissionRepository(_log);
        _vehicleReader = new FakeVehicleAvailabilityReader(_log);
        _driverReader = new FakeDriverEligibilityReader(_log);
        _vehicles = new FakeVehicleCommitments(_log);
        _drivers = new FakeDriverCommitments(_log);
        _audit = new LoggingAuditRecorder(_log);
        _vehicleReader.Snapshot = TestMissions.VehicleSnapshot(_vehicleId, VehicleType.HeavyTruck);
        _driverReader.Snapshot = TestMissions.DriverSnapshot(_driverId, VehicleType.HeavyTruck);
    }

    private Task<Result<Fserp.FleetOperations.Modules.Operations.Application.Views.MissionView>> Handle(Guid missionId) =>
        AssignMissionHandler.Handle(
            new AssignMission(missionId, _vehicleId, _driverId),
            _missions,
            _unitOfWork,
            _vehicleReader,
            _driverReader,
            _vehicles,
            _drivers,
            _audit,
            CancellationToken.None);

    private Mission GivenAScheduledMission(decimal requiredCapacityKg = 800m) =>
        _missions.Store(TestMissions.Scheduled(requiredCapacityKg));

    [Fact]
    public async Task The_seven_steps_run_in_the_plans_order_and_in_no_other()
    {
        // The guarantee this pins: a later refactor cannot reorder steps 4 and 5, nor hoist a commitment
        // above mission.Assign, without failing here. Order is a correctness property: the vehicle
        // snapshot of step 2 supplies the type step 5 checks, and step 3 must refuse an unassignable
        // mission before any resource is touched.
        var mission = GivenAScheduledMission();

        var result = await Handle(mission.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            [
                FakeMissionRepository.LoadCall,
                FakeVehicleAvailabilityReader.ReadCall,
                FakeDriverEligibilityReader.ReadCall,
                FakeVehicleCommitments.CommitCall,
                FakeDriverCommitments.CommitCall,
                LoggingAuditRecorder.RecordCall,
            ],
            _log.Calls);
    }

    [Fact]
    public async Task Step_3_changes_the_mission_before_either_commitment_is_called()
    {
        // The call log shows the two commitments after the load and the reads; this shows the aggregate's
        // own transition happened before them, which the log alone cannot say because Assign is not a port.
        var mission = GivenAScheduledMission();
        var statusWhenVehicleWasCommitted = default(MissionStatus?);
        _vehicles.CommitThrows = null;
        var probing = new ProbingVehicleCommitments(_vehicles, () => statusWhenVehicleWasCommitted = mission.Status);

        var result = await AssignMissionHandler.Handle(
            new AssignMission(mission.Id, _vehicleId, _driverId),
            _missions,
            _unitOfWork,
            _vehicleReader,
            _driverReader,
            probing,
            _drivers,
            _audit,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(MissionStatus.Assigned, statusWhenVehicleWasCommitted);
    }

    [Fact]
    public async Task The_vehicle_snapshot_supplies_the_type_the_driver_commitment_checks()
    {
        // Step 2's purpose beyond the 404: precondition 8 is "qualified for the vehicle", and the type
        // comes from the snapshot rather than from the caller, who could otherwise name any type.
        _vehicleReader.Snapshot = TestMissions.VehicleSnapshot(_vehicleId, VehicleType.Van);
        var mission = GivenAScheduledMission();

        await Handle(mission.Id);

        var commit = Assert.Single(_drivers.Commits);
        Assert.Equal(VehicleType.Van, commit.VehicleType);
    }

    [Fact]
    public async Task The_vehicle_commitment_is_asked_for_the_missions_required_capacity()
    {
        // Precondition 4 is checked against the mission's own requirement, not against anything the caller
        // sent with the assignment.
        var mission = GivenAScheduledMission(requiredCapacityKg: 2750.5m);

        await Handle(mission.Id);

        var commit = Assert.Single(_vehicles.Commits);
        Assert.Equal(2750.5m, commit.RequiredCapacityKg);
        Assert.Equal(mission.Id, commit.MissionId);
        Assert.Equal(_vehicleId, commit.VehicleId);
    }

    [Fact]
    public async Task A_successful_assignment_records_the_action_with_the_vehicle_and_driver_ids()
    {
        var mission = GivenAScheduledMission();

        await Handle(mission.Id);

        var recorded = Assert.Single(_audit.Actions);
        Assert.Equal("Operations", recorded.Module);
        Assert.Equal("MissionAssigned", recorded.Action);
        Assert.Equal("Mission", recorded.EntityType);
        Assert.Equal(mission.Id.ToString(), recorded.EntityId);
        Assert.Equal(_vehicleId.ToString(), recorded.Metadata!["vehicleId"]);
        Assert.Equal(_driverId.ToString(), recorded.Metadata["driverId"]);
        Assert.Empty(_audit.Attempts);
    }

    [Fact]
    public async Task The_handler_never_saves_because_the_middleware_owns_the_transaction()
    {
        var mission = GivenAScheduledMission();

        await Handle(mission.Id);

        Assert.Equal(0, _unitOfWork.Saves);
    }

    // ---- Step 1: unknown mission ----

    [Fact]
    public async Task An_unknown_mission_is_404_and_nothing_else_is_touched()
    {
        var result = await Handle(Guid.CreateVersion7());

        Assert.True(result.IsFailure);
        Assert.Equal("MISSION_NOT_FOUND", result.FailureDescriptor!.Identity.Code);
        Assert.Equal("operations", result.FailureDescriptor.Identity.Domain);
        Assert.Equal(ErrorCategory.NotFound, result.FailureDescriptor.Category);
        Assert.Equal([FakeMissionRepository.LoadCall], _log.Calls);
    }

    [Fact]
    public async Task An_unknown_mission_records_no_attempt_at_all()
    {
        // L-22, the line the lead drew: a request naming a mission that does not exist was never an
        // attempt on a mission, and there is no entity id to record it against.
        await Handle(Guid.CreateVersion7());

        Assert.Empty(_audit.Attempts);
        Assert.Empty(_audit.Actions);
    }

    // ---- Step 2: unknown vehicle or driver ----

    [Fact]
    public async Task An_unknown_vehicle_is_404_and_is_recorded_as_a_rejected_attempt()
    {
        var mission = GivenAScheduledMission();
        _vehicleReader.Snapshot = null;

        var result = await Handle(mission.Id);

        Assert.True(result.IsFailure);
        Assert.Equal("MISSION_VEHICLE_NOT_FOUND", result.FailureDescriptor!.Identity.Code);
        Assert.Equal(ErrorCategory.NotFound, result.FailureDescriptor.Category);
        // Nothing was changed and the driver was never even read.
        Assert.Equal(MissionStatus.Scheduled, mission.Status);
        Assert.Empty(_vehicles.Commits);
        Assert.Empty(_drivers.Commits);
        Assert.Null(_driverReader.AskedFor);

        var attempt = Assert.Single(_audit.Attempts);
        Assert.Equal(AuditOutcome.Rejected, attempt.Outcome);
        Assert.Equal("MissionAssigned", attempt.Action);
        Assert.Equal(mission.Id.ToString(), attempt.EntityId);
        Assert.Equal(new AuditFailure("operations", "MISSION_VEHICLE_NOT_FOUND"), attempt.Failure);
    }

    [Fact]
    public async Task An_unknown_driver_is_404_and_is_recorded_as_a_rejected_attempt()
    {
        var mission = GivenAScheduledMission();
        _driverReader.Snapshot = null;

        var result = await Handle(mission.Id);

        Assert.True(result.IsFailure);
        Assert.Equal("MISSION_DRIVER_NOT_FOUND", result.FailureDescriptor!.Identity.Code);
        Assert.Equal(MissionStatus.Scheduled, mission.Status);
        Assert.Empty(_vehicles.Commits);

        var attempt = Assert.Single(_audit.Attempts);
        Assert.Equal(new AuditFailure("operations", "MISSION_DRIVER_NOT_FOUND"), attempt.Failure);
        Assert.Equal(_vehicleId.ToString(), attempt.Metadata!["vehicleId"]);
        Assert.Equal(_driverId.ToString(), attempt.Metadata["driverId"]);
    }

    // ---- Step 3: precondition 1 ----

    [Theory]
    [InlineData(MissionStatus.Draft)]
    [InlineData(MissionStatus.Assigned)]
    [InlineData(MissionStatus.InProgress)]
    [InlineData(MissionStatus.Completed)]
    [InlineData(MissionStatus.Cancelled)]
    public async Task A_mission_that_is_not_scheduled_is_refused_before_any_resource_is_touched(MissionStatus status)
    {
        var mission = _missions.Store(TestMissions.In(status, _vehicleId, _driverId));

        var refused = await Assert.ThrowsAsync<BusinessRuleValidationException>(() => Handle(mission.Id));

        Assert.Equal("MISSION_NOT_ASSIGNABLE", refused.Rule.Code);
        Assert.Equal(status, mission.Status);
        Assert.Empty(_vehicles.Commits);
        Assert.Empty(_drivers.Commits);

        var attempt = Assert.Single(_audit.Attempts);
        Assert.Equal(new AuditFailure("operations", "MISSION_NOT_ASSIGNABLE"), attempt.Failure);
        Assert.Empty(_audit.Actions);
    }

    // ---- Steps 4 and 5: the other modules' rules ----

    [Fact]
    public async Task Assigning_a_vehicle_under_maintenance_is_refused_and_the_attempt_is_audited()
    {
        // The canonical case named in docs/plans/operations.md, "Business audit": this is the challenge's
        // "a rejected operation is audited" proof. The refusal is the real Fleet rule, raised by the real
        // Vehicle aggregate rather than by a string the test invented.
        var underMaintenance = TestVehicleStates.UnderMaintenance();
        var mission = GivenAScheduledMission();
        _vehicles.CommitThrows = () => TestMissions.Broken(
            new VehicleUnderMaintenanceRule(underMaintenance));

        var refused = await Assert.ThrowsAsync<BusinessRuleValidationException>(() => Handle(mission.Id));

        Assert.Equal("VEHICLE_UNDER_MAINTENANCE", refused.Rule.Code);
        Assert.Equal("fleet", refused.Rule.ErrorDomain);

        var attempt = Assert.Single(_audit.Attempts);
        Assert.Equal(AuditOutcome.Rejected, attempt.Outcome);
        Assert.Equal("Operations", attempt.Module);
        Assert.Equal("MissionAssigned", attempt.Action);
        Assert.Equal("Mission", attempt.EntityType);
        Assert.Equal(mission.Id.ToString(), attempt.EntityId);
        // The attempt carries the refusing module's own domain and code, not a translation of it.
        Assert.Equal(new AuditFailure("fleet", "VEHICLE_UNDER_MAINTENANCE"), attempt.Failure);
        Assert.Equal(_vehicleId.ToString(), attempt.Metadata!["vehicleId"]);
        Assert.Equal(_driverId.ToString(), attempt.Metadata["driverId"]);
        // Nothing succeeded, so no successful action was recorded and the driver was never committed.
        Assert.Empty(_audit.Actions);
        Assert.Empty(_drivers.Commits);
    }

    [Fact]
    public async Task A_driver_refusal_is_audited_with_the_drivers_own_domain_and_code()
    {
        var mission = GivenAScheduledMission();
        _drivers.CommitThrows = () => TestMissions.Broken(
            new DriverNotActiveRule(Fserp.FleetOperations.Modules.Drivers.Domain.OperationalStatus.Inactive));

        var refused = await Assert.ThrowsAsync<BusinessRuleValidationException>(() => Handle(mission.Id));

        Assert.Equal("DRIVER_NOT_ACTIVE", refused.Rule.Code);
        var attempt = Assert.Single(_audit.Attempts);
        Assert.Equal(new AuditFailure("drivers", "DRIVER_NOT_ACTIVE"), attempt.Failure);
        // The vehicle commitment had already run; the middleware's rollback is what undoes it, which is
        // why nothing here tries to compensate.
        Assert.Single(_vehicles.Commits);
        Assert.Empty(_audit.Actions);
    }

    public static TheoryData<string, string> EveryCommitmentRefusal() => new()
    {
        { "vehicle", "VEHICLE_NOT_ACTIVE" },
        { "vehicle", "VEHICLE_UNDER_MAINTENANCE" },
        { "vehicle", "VEHICLE_CAPACITY_INSUFFICIENT" },
        { "vehicle", "VEHICLE_NOT_AVAILABLE" },
        { "driver", "DRIVER_NOT_ACTIVE" },
        { "driver", "DRIVER_NOT_AVAILABLE" },
        { "driver", "DRIVER_NOT_QUALIFIED" },
    };

    [Theory]
    [MemberData(nameof(EveryCommitmentRefusal))]
    public async Task Every_rule_refusal_from_either_commitment_port_is_audited(string port, string code)
    {
        // L-22: a rejected attempt for every outcome from step 2 onward, which includes all seven rules of
        // preconditions 2 to 8. Each is raised through the real rule class of the owning module.
        var mission = GivenAScheduledMission();
        var broken = () => TestMissions.Broken(RuleFor(code));
        if (port == "vehicle")
        {
            _vehicles.CommitThrows = broken;
        }
        else
        {
            _drivers.CommitThrows = broken;
        }

        var refused = await Assert.ThrowsAsync<BusinessRuleValidationException>(() => Handle(mission.Id));

        Assert.Equal(code, refused.Rule.Code);
        var attempt = Assert.Single(_audit.Attempts);
        Assert.Equal(new AuditFailure(port == "vehicle" ? "fleet" : "drivers", code), attempt.Failure);
        Assert.Equal(mission.Id.ToString(), attempt.EntityId);
        Assert.Empty(_audit.Actions);
    }

    private static IBusinessRule RuleFor(string code) => code switch
    {
        "VEHICLE_NOT_ACTIVE" => new VehicleNotActiveRule(
            Fserp.FleetOperations.Modules.Fleet.Domain.OperationalStatus.Inactive),
        "VEHICLE_UNDER_MAINTENANCE" => new VehicleUnderMaintenanceRule(TestVehicleStates.UnderMaintenance()),
        "VEHICLE_CAPACITY_INSUFFICIENT" => new VehicleCapacityInsufficientRule(100m, 900m),
        "VEHICLE_NOT_AVAILABLE" => new VehicleNotAvailableRule(Guid.CreateVersion7(), Guid.CreateVersion7()),
        "DRIVER_NOT_ACTIVE" => new DriverNotActiveRule(
            Fserp.FleetOperations.Modules.Drivers.Domain.OperationalStatus.Inactive),
        "DRIVER_NOT_AVAILABLE" => new DriverNotAvailableRule(Guid.CreateVersion7(), Guid.CreateVersion7()),
        "DRIVER_NOT_QUALIFIED" => new DriverNotQualifiedRule([], VehicleType.HeavyTruck),
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "No rule for this code."),
    };

    // ---- L-23: the resource disappeared between step 2 and steps 4 to 5 ----

    [Fact]
    public async Task A_vehicle_that_disappears_after_the_snapshot_reaches_the_caller_as_404_and_not_500()
    {
        // L-23, made explicit and tested: the reader answered with a snapshot, and the repository behind
        // IVehicleCommitments then found nothing, so the port raises Fleet's own VEHICLE_NOT_FOUND as a
        // ResultFailureException. Letting it through keeps it a 404 — the edge maps a ResultFailureException
        // to the status its descriptor names — instead of a 500.
        var mission = GivenAScheduledMission();
        _vehicles.CommitThrows = () => TestMissions.Missing("fleet", "VEHICLE_NOT_FOUND");

        var thrown = await Assert.ThrowsAsync<ResultFailureException>(() => Handle(mission.Id));

        Assert.Equal("fleet", thrown.Failure.Identity.Domain);
        Assert.Equal("VEHICLE_NOT_FOUND", thrown.Failure.Identity.Code);
        Assert.Equal(ErrorCategory.NotFound, thrown.Failure.Category);

        var attempt = Assert.Single(_audit.Attempts);
        Assert.Equal(new AuditFailure("fleet", "VEHICLE_NOT_FOUND"), attempt.Failure);
        Assert.Equal(mission.Id.ToString(), attempt.EntityId);
    }

    [Fact]
    public async Task A_driver_that_disappears_after_the_snapshot_reaches_the_caller_as_404_and_not_500()
    {
        var mission = GivenAScheduledMission();
        _drivers.CommitThrows = () => TestMissions.Missing("drivers", "DRIVER_NOT_FOUND");

        var thrown = await Assert.ThrowsAsync<ResultFailureException>(() => Handle(mission.Id));

        Assert.Equal("drivers", thrown.Failure.Identity.Domain);
        Assert.Equal("DRIVER_NOT_FOUND", thrown.Failure.Identity.Code);
        Assert.Equal(ErrorCategory.NotFound, thrown.Failure.Category);
        Assert.Equal(new AuditFailure("drivers", "DRIVER_NOT_FOUND"), Assert.Single(_audit.Attempts).Failure);
    }

    [Fact]
    public void The_handler_declares_ports_only_and_the_unit_of_work()
    {
        // The shape the plan names: one handler, one IUnitOfWork, the two readers, the two commitment
        // ports, the repository and the recorder. Nothing else, and nothing concrete.
        var parameters = typeof(AssignMissionHandler)
            .GetMethod(nameof(AssignMissionHandler.Handle))!
            .GetParameters()
            .Skip(1)
            .Select(parameter => parameter.ParameterType)
            .Where(type => type != typeof(CancellationToken))
            .ToList();

        Assert.All(parameters, type => Assert.True(type.IsInterface, $"{type.Name} is not a port."));
        Assert.Equal(
            [
                "IBusinessAuditRecorder", "IDriverCommitments", "IDriverEligibilityReader", "IMissionRepository",
                "IUnitOfWork", "IVehicleAvailabilityReader", "IVehicleCommitments",
            ],
            parameters.Select(type => type.Name).Order());
    }
}

/// <summary>
/// Wraps the vehicle commitment port so a test can observe the mission's state at the moment step 4 runs.
/// </summary>
internal sealed class ProbingVehicleCommitments(IVehicleCommitments inner, Action probe) : IVehicleCommitments
{
    public Task CommitToMissionAsync(Guid vehicleId, Guid missionId, decimal requiredCapacityKg, CancellationToken cancellationToken)
    {
        probe();
        return inner.CommitToMissionAsync(vehicleId, missionId, requiredCapacityKg, cancellationToken);
    }

    public Task ReleaseFromMissionAsync(Guid vehicleId, Guid missionId, CancellationToken cancellationToken)
    {
        probe();
        return inner.ReleaseFromMissionAsync(vehicleId, missionId, cancellationToken);
    }
}

/// <summary>The Fleet and Drivers states the Operations tests need to name, without reaching into them.</summary>
internal static class TestVehicleStates
{
    public static MaintenanceStatus UnderMaintenance() => MaintenanceStatus.UnderMaintenance;
}
