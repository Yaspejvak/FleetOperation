using Fserp.FleetOperations.Modules.Fleet.Application;
using Fserp.FleetOperations.Modules.Fleet.Application.Commands;
using Fserp.FleetOperations.Modules.Fleet.Application.Validators;
using Fserp.FleetOperations.Modules.Fleet.Application.Views;
using Fserp.FleetOperations.Modules.Fleet.Domain;
using Fserp.FleetOperations.Modules.Fleet.Domain.Events;
using MPCore.Application.Results;
using MPCore.Audit;
using MPCore.Domain.Rules;

namespace Fserp.FleetOperations.UnitTests.Fleet;

/// <summary>
/// The two maintenance transitions on the aggregate (docs/plans/fleet.md, round 2): each rule, that a
/// refusal changes nothing and raises nothing, F-4 (an Inactive vehicle may start maintenance) and F-1
/// (Complete Maintenance touches only the maintenance field).
/// </summary>
public sealed class VehicleMaintenanceTests
{
    private static Vehicle UnderMaintenance()
    {
        var vehicle = TestVehicles.Registered();
        vehicle.StartMaintenance();
        vehicle.ClearEvents();
        return vehicle;
    }

    [Fact]
    public void Starting_maintenance_sets_the_maintenance_field_and_raises_MaintenanceStarted()
    {
        var vehicle = TestVehicles.Registered();

        vehicle.StartMaintenance();

        Assert.Equal(MaintenanceStatus.UnderMaintenance, vehicle.MaintenanceStatus);
        Assert.Equal(new MaintenanceStarted(vehicle.Id), Assert.Single(vehicle.DomainEvents));
    }

    [Fact]
    public void Starting_maintenance_leaves_the_operational_status_untouched()
    {
        // F-1: two independent fields. Start Maintenance never writes the operational status.
        var vehicle = TestVehicles.Registered();

        vehicle.StartMaintenance();

        Assert.Equal(OperationalStatus.Active, vehicle.OperationalStatus);
        Assert.Equal(VehicleDisplayStatus.UnderMaintenance, vehicle.DisplayStatus);
    }

    [Fact]
    public void An_Inactive_vehicle_may_start_maintenance()
    {
        // F-4, decided: there is no operational-status check on Start Maintenance.
        var vehicle = TestVehicles.Registered();
        vehicle.ChangeStatus(OperationalStatus.Inactive);
        vehicle.ClearEvents();

        vehicle.StartMaintenance();

        Assert.Equal(MaintenanceStatus.UnderMaintenance, vehicle.MaintenanceStatus);
        Assert.Equal(OperationalStatus.Inactive, vehicle.OperationalStatus);
        Assert.Equal(new MaintenanceStarted(vehicle.Id), Assert.Single(vehicle.DomainEvents));
    }

    [Fact]
    public void Starting_maintenance_twice_is_refused_and_changes_nothing()
    {
        var vehicle = UnderMaintenance();

        var refused = Assert.Throws<BusinessRuleValidationException>(vehicle.StartMaintenance);

        Assert.Equal("fleet", refused.Rule.ErrorDomain);
        Assert.Equal("VEHICLE_ALREADY_UNDER_MAINTENANCE", refused.Rule.Code);
        Assert.Equal("fleet.vehicle_already_under_maintenance", refused.Rule.MessageKey);
        Assert.Equal(MaintenanceStatus.UnderMaintenance, vehicle.MaintenanceStatus);
        Assert.Equal(OperationalStatus.Active, vehicle.OperationalStatus);
        Assert.Empty(vehicle.DomainEvents);
    }

    [Fact]
    public void A_committed_vehicle_cannot_start_maintenance_and_nothing_changes()
    {
        // Decision 3: the refusal is the only behaviour that keeps every invariant true.
        var mission = Guid.CreateVersion7();
        var vehicle = TestVehicles.Registered().CommittedTo(mission);

        var refused = Assert.Throws<BusinessRuleValidationException>(vehicle.StartMaintenance);

        Assert.Equal("VEHICLE_HAS_MISSION_COMMITMENT", refused.Rule.Code);
        Assert.Equal(MaintenanceStatus.NotUnderMaintenance, vehicle.MaintenanceStatus);
        Assert.Equal(mission, vehicle.CommittedMissionId);
        Assert.Empty(vehicle.DomainEvents);
    }

    [Fact]
    public void A_vehicle_that_is_both_under_maintenance_and_committed_answers_the_maintenance_rule()
    {
        // L-5: the order is fixed for determinism, although the two states cannot both hold in a
        // consistent aggregate. This pins the decided order rather than leaving it to chance.
        var vehicle = UnderMaintenance().CommittedTo(Guid.CreateVersion7());

        var refused = Assert.Throws<BusinessRuleValidationException>(vehicle.StartMaintenance);

        Assert.Equal("VEHICLE_ALREADY_UNDER_MAINTENANCE", refused.Rule.Code);
    }

    [Fact]
    public void Completing_maintenance_clears_the_maintenance_field_and_raises_MaintenanceCompleted()
    {
        var vehicle = UnderMaintenance();

        vehicle.CompleteMaintenance();

        Assert.Equal(MaintenanceStatus.NotUnderMaintenance, vehicle.MaintenanceStatus);
        Assert.Equal(new MaintenanceCompleted(vehicle.Id), Assert.Single(vehicle.DomainEvents));
    }

    [Theory]
    [InlineData(OperationalStatus.Active, VehicleDisplayStatus.Active)]
    [InlineData(OperationalStatus.Inactive, VehicleDisplayStatus.Inactive)]
    public void Completing_maintenance_touches_only_the_maintenance_field(
        OperationalStatus operational,
        VehicleDisplayStatus displayed)
    {
        // F-1, decided (owner): the operational status is untouched, so an Inactive vehicle that leaves
        // maintenance is Inactive, not Active.
        var vehicle = TestVehicles.Registered();
        vehicle.ChangeStatus(operational);
        vehicle.StartMaintenance();
        var mission = vehicle.CommittedMissionId;
        vehicle.ClearEvents();

        vehicle.CompleteMaintenance();

        Assert.Equal(operational, vehicle.OperationalStatus);
        Assert.Equal(MaintenanceStatus.NotUnderMaintenance, vehicle.MaintenanceStatus);
        Assert.Equal(displayed, vehicle.DisplayStatus);
        Assert.Equal(mission, vehicle.CommittedMissionId);
    }

    [Fact]
    public void Completing_maintenance_on_a_vehicle_not_under_maintenance_is_refused_and_changes_nothing()
    {
        var vehicle = TestVehicles.Registered();

        var refused = Assert.Throws<BusinessRuleValidationException>(vehicle.CompleteMaintenance);

        Assert.Equal("fleet", refused.Rule.ErrorDomain);
        Assert.Equal("VEHICLE_NOT_UNDER_MAINTENANCE", refused.Rule.Code);
        Assert.Equal("fleet.vehicle_not_under_maintenance", refused.Rule.MessageKey);
        Assert.Equal(MaintenanceStatus.NotUnderMaintenance, vehicle.MaintenanceStatus);
        Assert.Equal(OperationalStatus.Active, vehicle.OperationalStatus);
        Assert.Empty(vehicle.DomainEvents);
    }

    [Fact]
    public void Completing_maintenance_twice_is_refused_the_second_time()
    {
        // Neither maintenance command has a no-op path, unlike ChangeStatus (F-8).
        var vehicle = UnderMaintenance();
        vehicle.CompleteMaintenance();

        Assert.Throws<BusinessRuleValidationException>(vehicle.CompleteMaintenance);
    }

    [Fact]
    public void A_committed_vehicle_may_complete_maintenance()
    {
        // The commitment rule is on Start Maintenance only (docs/plans/fleet.md, "Invariants"); no rule
        // is invented for Complete.
        var vehicle = UnderMaintenance().CommittedTo(Guid.CreateVersion7());

        vehicle.CompleteMaintenance();

        Assert.Equal(MaintenanceStatus.NotUnderMaintenance, vehicle.MaintenanceStatus);
    }
}

/// <summary>
/// The two maintenance commands against fakes of their ports: the not-found failure, the required
/// success audit, and the rejected-attempt audit that docs/plans/fleet.md requires for both
/// Start Maintenance refusals and for neither Complete Maintenance refusal.
/// </summary>
public sealed class MaintenanceHandlerTests
{
    private readonly FakeVehicleRepository _vehicles = new();
    private readonly FakeAuditRecorder _audit = new();
    private readonly FakeUnitOfWork _unitOfWork = new();

    private Vehicle Stored(Vehicle vehicle)
    {
        _vehicles.Stored[vehicle.Id] = vehicle;
        return vehicle;
    }

    private Task<Result<VehicleView>> Start(Guid vehicleId) =>
        StartMaintenanceHandler.Handle(new StartMaintenance(vehicleId), _vehicles, _unitOfWork, _audit, CancellationToken.None);

    private Task<Result<VehicleView>> Complete(Guid vehicleId) =>
        CompleteMaintenanceHandler.Handle(new CompleteMaintenance(vehicleId), _vehicles, _unitOfWork, _audit, CancellationToken.None);

    private Vehicle StoredUnderMaintenance(string plate = "AB-123")
    {
        var vehicle = Stored(TestVehicles.Registered(plate));
        vehicle.StartMaintenance();
        vehicle.ClearEvents();
        return vehicle;
    }

    [Fact]
    public async Task Start_on_an_unknown_vehicle_is_a_404_and_audits_nothing()
    {
        var result = await Start(Guid.CreateVersion7());

        Assert.True(result.IsFailure);
        Assert.Equal(new ErrorIdentity("fleet", "VEHICLE_NOT_FOUND"), result.FailureDescriptor!.Identity);
        Assert.Equal(ErrorCategory.NotFound, result.FailureDescriptor.Category);
        Assert.Equal("fleet.vehicle_not_found", result.FailureDescriptor.Message.Key);
        Assert.Empty(_audit.Actions);
        Assert.Empty(_audit.Attempts);
    }

    [Fact]
    public async Task Complete_on_an_unknown_vehicle_is_a_404_and_audits_nothing()
    {
        var result = await Complete(Guid.CreateVersion7());

        Assert.True(result.IsFailure);
        Assert.Equal(new ErrorIdentity("fleet", "VEHICLE_NOT_FOUND"), result.FailureDescriptor!.Identity);
        Assert.Equal(ErrorCategory.NotFound, result.FailureDescriptor.Category);
        Assert.Empty(_audit.Actions);
        Assert.Empty(_audit.Attempts);
    }

    [Fact]
    public async Task Start_returns_the_view_and_records_MaintenanceStarted()
    {
        var vehicle = Stored(TestVehicles.Registered());

        var result = await Start(vehicle.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(MaintenanceStatus.UnderMaintenance, result.Value.MaintenanceStatus);
        Assert.Equal(OperationalStatus.Active, result.Value.OperationalStatus);
        Assert.Equal(VehicleDisplayStatus.UnderMaintenance, result.Value.DisplayStatus);
        Assert.Equal(
            new RecordedAction("Fleet", "MaintenanceStarted", "Vehicle", vehicle.Id.ToString(), null),
            Assert.Single(_audit.Actions));
        Assert.Empty(_audit.Attempts);
    }

    [Fact]
    public async Task Complete_returns_the_view_and_records_MaintenanceCompleted()
    {
        var vehicle = StoredUnderMaintenance();

        var result = await Complete(vehicle.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(MaintenanceStatus.NotUnderMaintenance, result.Value.MaintenanceStatus);
        Assert.Equal(
            new RecordedAction("Fleet", "MaintenanceCompleted", "Vehicle", vehicle.Id.ToString(), null),
            Assert.Single(_audit.Actions));
        Assert.Empty(_audit.Attempts);
    }

    [Fact]
    public async Task Start_on_a_vehicle_already_under_maintenance_records_the_rejected_attempt_and_rethrows()
    {
        // docs/plans/fleet.md, "Business audit": Start Maintenance refused with
        // VEHICLE_ALREADY_UNDER_MAINTENANCE is recorded as a rejected attempt.
        var vehicle = StoredUnderMaintenance();

        var refused = await Assert.ThrowsAsync<BusinessRuleValidationException>(() => Start(vehicle.Id));

        Assert.Equal("VEHICLE_ALREADY_UNDER_MAINTENANCE", refused.Rule.Code);
        Assert.Empty(_audit.Actions);
        var attempt = Assert.Single(_audit.Attempts);
        Assert.Equal(
            new RecordedAttempt(
                "Fleet",
                "MaintenanceStarted",
                AuditOutcome.Rejected,
                new AuditFailure("fleet", "VEHICLE_ALREADY_UNDER_MAINTENANCE"),
                "Vehicle",
                vehicle.Id.ToString(),
                null),
            attempt);
    }

    [Fact]
    public async Task Start_on_a_committed_vehicle_records_the_rejected_attempt_and_rethrows()
    {
        // docs/plans/fleet.md, "Business audit": the other refusal that must be recorded (decision 3).
        var vehicle = Stored(TestVehicles.Registered().CommittedTo(Guid.CreateVersion7()));

        var refused = await Assert.ThrowsAsync<BusinessRuleValidationException>(() => Start(vehicle.Id));

        Assert.Equal("VEHICLE_HAS_MISSION_COMMITMENT", refused.Rule.Code);
        Assert.Equal(MaintenanceStatus.NotUnderMaintenance, vehicle.MaintenanceStatus);
        Assert.Empty(vehicle.DomainEvents);
        Assert.Empty(_audit.Actions);
        var attempt = Assert.Single(_audit.Attempts);
        Assert.Equal(
            new RecordedAttempt(
                "Fleet",
                "MaintenanceStarted",
                AuditOutcome.Rejected,
                new AuditFailure("fleet", "VEHICLE_HAS_MISSION_COMMITMENT"),
                "Vehicle",
                vehicle.Id.ToString(),
                null),
            attempt);
    }

    [Fact]
    public async Task Complete_refused_records_no_attempt()
    {
        // fleet.md requires no rejected-attempt audit for Complete Maintenance, so none is invented.
        var vehicle = Stored(TestVehicles.Registered());

        var refused = await Assert.ThrowsAsync<BusinessRuleValidationException>(() => Complete(vehicle.Id));

        Assert.Equal("VEHICLE_NOT_UNDER_MAINTENANCE", refused.Rule.Code);
        Assert.Empty(_audit.Actions);
        Assert.Empty(_audit.Attempts);
    }

    [Fact]
    public async Task Neither_command_saves_itself_on_any_path()
    {
        // The middleware owns the transaction; a handler cannot half-commit its own work. Every path is
        // walked: success, not found, and each refusal. The refusal vehicles are kept apart from the
        // success ones, so no earlier call in this test puts them in the state under test.
        var started = Stored(TestVehicles.Registered("AB-1"));
        var maintained = StoredUnderMaintenance("AB-2");
        var neverMaintained = Stored(TestVehicles.Registered("AB-3"));
        var committed = Stored(TestVehicles.Registered("AB-4").CommittedTo(Guid.CreateVersion7()));

        await Start(started.Id);
        await Complete(maintained.Id);
        await Start(Guid.CreateVersion7());
        await Complete(Guid.CreateVersion7());
        await Assert.ThrowsAsync<BusinessRuleValidationException>(() => Complete(neverMaintained.Id));
        await Assert.ThrowsAsync<BusinessRuleValidationException>(() => Start(started.Id));
        await Assert.ThrowsAsync<BusinessRuleValidationException>(() => Start(committed.Id));

        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task A_refused_start_raises_no_event()
    {
        var vehicle = StoredUnderMaintenance();

        await Assert.ThrowsAsync<BusinessRuleValidationException>(() => Start(vehicle.Id));

        Assert.Empty(vehicle.DomainEvents);
    }

    [Fact]
    public async Task A_successful_start_raises_MaintenanceStarted_for_delivery_after_commit()
    {
        var vehicle = Stored(TestVehicles.Registered());

        await Start(vehicle.Id);

        Assert.Equal(new MaintenanceStarted(vehicle.Id), Assert.Single(vehicle.DomainEvents));
    }

    [Fact]
    public async Task A_successful_complete_raises_MaintenanceCompleted_for_delivery_after_commit()
    {
        var vehicle = StoredUnderMaintenance();

        await Complete(vehicle.Id);

        Assert.Equal(new MaintenanceCompleted(vehicle.Id), Assert.Single(vehicle.DomainEvents));
    }

    [Fact]
    public void The_audit_action_names_are_the_ones_the_plan_names()
    {
        Assert.Equal("MaintenanceStarted", FleetAudit.MaintenanceStarted);
        Assert.Equal("MaintenanceCompleted", FleetAudit.MaintenanceCompleted);
    }
}

/// <summary>The input shape of the two maintenance commands: a non-empty identity, nothing else.</summary>
public sealed class MaintenanceValidatorTests
{
    [Fact]
    public void Start_refuses_an_empty_identity_on_the_vehicle_id_field()
    {
        var result = new StartMaintenanceValidator().Validate(new StartMaintenance(Guid.Empty));

        Assert.False(result.IsValid);
        Assert.Equal("VehicleId", Assert.Single(result.Errors).PropertyName);
    }

    [Fact]
    public void Complete_refuses_an_empty_identity_on_the_vehicle_id_field()
    {
        var result = new CompleteMaintenanceValidator().Validate(new CompleteMaintenance(Guid.Empty));

        Assert.False(result.IsValid);
        Assert.Equal("VehicleId", Assert.Single(result.Errors).PropertyName);
    }

    [Fact]
    public void Both_accept_a_real_identity()
    {
        var id = Guid.CreateVersion7();

        Assert.True(new StartMaintenanceValidator().Validate(new StartMaintenance(id)).IsValid);
        Assert.True(new CompleteMaintenanceValidator().Validate(new CompleteMaintenance(id)).IsValid);
    }
}
