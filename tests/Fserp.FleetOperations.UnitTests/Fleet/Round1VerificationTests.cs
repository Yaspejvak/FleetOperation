using System.Reflection;
using Fserp.FleetOperations.Modules.Fleet.Application.Commands;
using Fserp.FleetOperations.Modules.Fleet.Application.Views;
using Fserp.FleetOperations.Modules.Fleet.Contracts;
using Fserp.FleetOperations.Modules.Fleet.Domain;
using Fserp.FleetOperations.Modules.Fleet.Domain.Events;
using MPCore.Application.Results;
using MPCore.Audit;
using MPCore.Domain.Rules;

namespace Fserp.FleetOperations.UnitTests.Fleet;

/// <summary>
/// Domain cases of docs/plans/fleet.md that the round 1 tests did not cover (fleet-verifier, round 1).
/// </summary>
public sealed class VehicleDomainVerificationTests
{
    private static Vehicle UnderMaintenance(Vehicle vehicle)
    {
        // No production path enters maintenance before round 2; the rule under test is the derivation of
        // the displayed status from the two fields, not how the maintenance field is reached.
        typeof(Vehicle)
            .GetProperty(nameof(Vehicle.MaintenanceStatus), BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(vehicle, MaintenanceStatus.UnderMaintenance);
        return vehicle;
    }

    [Theory]
    [InlineData(OperationalStatus.Active)]
    [InlineData(OperationalStatus.Inactive)]
    public void Under_maintenance_wins_the_displayed_status_whatever_the_operational_status(OperationalStatus operational)
    {
        // docs/plans/fleet.md, "The displayed status is derived: UnderMaintenance wins".
        var vehicle = TestVehicles.Registered();
        vehicle.ChangeStatus(operational);
        UnderMaintenance(vehicle);

        Assert.Equal(VehicleDisplayStatus.UnderMaintenance, vehicle.DisplayStatus);
        Assert.Equal(VehicleDisplayStatus.UnderMaintenance, VehicleView.From(vehicle).DisplayStatus);
    }

    [Fact]
    public void Change_status_leaves_the_maintenance_field_of_a_vehicle_under_maintenance_untouched()
    {
        // F-1: two independent fields; Change Status never touches maintenance. F-4 allows Inactive under maintenance.
        var vehicle = UnderMaintenance(TestVehicles.Registered());

        Assert.True(vehicle.ChangeStatus(OperationalStatus.Inactive));
        Assert.Equal(MaintenanceStatus.UnderMaintenance, vehicle.MaintenanceStatus);
        Assert.True(vehicle.ChangeStatus(OperationalStatus.Active));
        Assert.Equal(MaintenanceStatus.UnderMaintenance, vehicle.MaintenanceStatus);
    }

    [Fact]
    public void A_refused_deactivation_leaves_the_commitment_in_place()
    {
        var mission = Guid.CreateVersion7();
        var vehicle = TestVehicles.Registered().CommittedTo(mission);

        Assert.Throws<BusinessRuleValidationException>(() => vehicle.ChangeStatus(OperationalStatus.Inactive));

        Assert.Equal(mission, vehicle.CommittedMissionId);
        Assert.Equal(VehicleDisplayStatus.Active, vehicle.DisplayStatus);
    }

    [Fact]
    public void Each_real_change_raises_exactly_one_event_and_the_no_op_none()
    {
        var vehicle = TestVehicles.Registered();

        vehicle.ChangeStatus(OperationalStatus.Inactive);
        vehicle.ChangeStatus(OperationalStatus.Inactive);
        vehicle.ChangeStatus(OperationalStatus.Active);
        vehicle.ChangeStatus(OperationalStatus.Active);

        Assert.Equal(
            [
                new VehicleStatusChanged(vehicle.Id, OperationalStatus.Active, OperationalStatus.Inactive),
                new VehicleStatusChanged(vehicle.Id, OperationalStatus.Inactive, OperationalStatus.Active),
            ],
            vehicle.DomainEvents.Cast<VehicleStatusChanged>());
    }
}

/// <summary>
/// Handler cases the round 1 tests did not cover: the other transition's audit, no save on any path,
/// nothing raised on a refusal, the clock as the only time source, and a broken plate rule reaching the
/// handler when the validator is bypassed (fleet-verifier, round 1).
/// </summary>
public sealed class HandlerVerificationTests
{
    private readonly FakeVehicleRepository _vehicles = new();
    private readonly FakeAuditRecorder _audit = new();
    private readonly FakeUnitOfWork _unitOfWork = new();

    private Vehicle Stored(Vehicle vehicle)
    {
        _vehicles.Stored[vehicle.Id] = vehicle;
        return vehicle;
    }

    private Task<Result<VehicleView>> Change(Guid id, OperationalStatus status) =>
        ChangeVehicleStatusHandler.Handle(new ChangeVehicleStatus(id, status), _vehicles, _unitOfWork, _audit, CancellationToken.None);

    private Task<Result<VehicleView>> Register(string plate, DateTimeOffset? now = null, decimal capacityKg = 1000m) =>
        RegisterVehicleHandler.Handle(
            new RegisterVehicle(plate, VehicleType.Van, capacityKg),
            _vehicles,
            _unitOfWork,
            new FixedClock(now ?? new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero)),
            _audit,
            CancellationToken.None);

    [Fact]
    public async Task Reactivation_is_recorded_with_from_Inactive_to_Active()
    {
        var vehicle = TestVehicles.Registered();
        vehicle.ChangeStatus(OperationalStatus.Inactive);
        vehicle.ClearEvents();
        Stored(vehicle);

        var result = await Change(vehicle.Id, OperationalStatus.Active);

        Assert.True(result.IsSuccess);
        Assert.Equal(OperationalStatus.Active, result.Value.OperationalStatus);
        var recorded = Assert.Single(_audit.Actions);
        Assert.Equal("VehicleStatusChanged", recorded.Action);
        Assert.Equal("Inactive", recorded.Metadata!["from"]);
        Assert.Equal("Active", recorded.Metadata["to"]);
        Assert.Equal(new VehicleStatusChanged(vehicle.Id, OperationalStatus.Inactive, OperationalStatus.Active), Assert.Single(vehicle.DomainEvents));
    }

    [Fact]
    public async Task Change_status_never_saves_itself_on_any_path()
    {
        var plain = Stored(TestVehicles.Registered("AB-1"));
        var committed = Stored(TestVehicles.Registered("AB-2").CommittedTo(Guid.CreateVersion7()));

        await Change(plain.Id, OperationalStatus.Inactive);
        await Change(plain.Id, OperationalStatus.Inactive);
        await Change(Guid.CreateVersion7(), OperationalStatus.Active);
        await Assert.ThrowsAsync<BusinessRuleValidationException>(() => Change(committed.Id, OperationalStatus.Inactive));

        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task A_refused_deactivation_raises_no_event_and_records_no_success()
    {
        var vehicle = Stored(TestVehicles.Registered().CommittedTo(Guid.CreateVersion7()));

        await Assert.ThrowsAsync<BusinessRuleValidationException>(() => Change(vehicle.Id, OperationalStatus.Inactive));

        Assert.Empty(vehicle.DomainEvents);
        Assert.Empty(_audit.Actions);
        Assert.Single(_audit.Attempts);
    }

    [Fact]
    public async Task The_no_op_on_a_committed_vehicle_records_no_attempt()
    {
        // F-8 wins over F-3 when nothing would change: Active to Active is a 200 without audit.
        var vehicle = Stored(TestVehicles.Registered().CommittedTo(Guid.CreateVersion7()));

        var result = await Change(vehicle.Id, OperationalStatus.Active);

        Assert.True(result.IsSuccess);
        Assert.Empty(_audit.Actions);
        Assert.Empty(_audit.Attempts);
        Assert.Empty(vehicle.DomainEvents);
    }

    [Fact]
    public async Task Registration_raises_VehicleRegistered_on_the_added_aggregate_for_delivery_after_commit()
    {
        var result = await Register("AB-9");

        var added = Assert.Single(_vehicles.Added);
        Assert.Equal(new VehicleRegistered(result.Value.Id), Assert.Single(added.DomainEvents));
    }

    [Fact]
    public async Task The_identity_takes_its_timestamp_from_the_clock()
    {
        // docs/plans/README.md: time comes only from IClock.
        var now = new DateTimeOffset(2031, 1, 2, 3, 4, 5, 678, TimeSpan.Zero);

        var result = await Register("AB-10", now);

        var bytes = result.Value.Id.ToByteArray(bigEndian: true);
        var milliseconds = ((long)bytes[0] << 40) | ((long)bytes[1] << 32) | ((long)bytes[2] << 24) | ((long)bytes[3] << 16) | ((long)bytes[4] << 8) | bytes[5];
        Assert.Equal(now.ToUnixTimeMilliseconds(), milliseconds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_plate_that_bypassed_the_validator_is_the_plate_rule_and_changes_nothing(string plate)
    {
        var refused = await Assert.ThrowsAsync<BusinessRuleValidationException>(() => Register(plate));

        Assert.Equal("fleet", refused.Rule.ErrorDomain);
        Assert.Equal("VEHICLE_PLATE_NUMBER_REQUIRED", refused.Rule.Code);
        Assert.Empty(_vehicles.Added);
        Assert.Empty(_audit.Actions);
        Assert.Empty(_audit.Attempts);
    }

    [Fact]
    public async Task A_duplicate_plate_records_no_attempt_and_saves_nothing()
    {
        // fleet.md lists no rejected-attempt audit for Register Vehicle; none is invented.
        await Register("AB-11");
        _audit.Actions.Clear();

        var result = await Register("ab-11");

        Assert.True(result.IsFailure);
        Assert.Empty(_audit.Actions);
        Assert.Empty(_audit.Attempts);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task The_rejected_attempt_names_the_vehicle_and_the_requested_change_and_nothing_else()
    {
        var vehicle = Stored(TestVehicles.Registered().CommittedTo(Guid.CreateVersion7()));

        await Assert.ThrowsAsync<BusinessRuleValidationException>(() => Change(vehicle.Id, OperationalStatus.Inactive));

        var attempt = Assert.Single(_audit.Attempts);
        Assert.Equal(AuditOutcome.Rejected, attempt.Outcome);
        Assert.Equal(["from", "to"], attempt.Metadata!.Keys.Order());
    }
}
