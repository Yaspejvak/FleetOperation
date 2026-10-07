using System.Reflection;
using Fserp.FleetOperations.Modules.Fleet.Application;
using Fserp.FleetOperations.Modules.Fleet.Application.Contracts;
using Fserp.FleetOperations.Modules.Fleet.Application.Ports;
using Fserp.FleetOperations.Modules.Fleet.Application.Views;
using Fserp.FleetOperations.Modules.Fleet.Contracts;
using Fserp.FleetOperations.Modules.Fleet.Domain;
using Fserp.FleetOperations.Modules.Fleet.Domain.Events;
using MPCore.Application.Results;
using MPCore.Domain.Rules;
using MPCore.Persistence.Abstractions;

namespace Fserp.FleetOperations.UnitTests.Fleet;

/// <summary>
/// <c>Vehicle.CommitToMission</c> and <c>Vehicle.ReleaseFromMission</c>: assignment preconditions 2 to 5
/// of docs/architecture.md, decision 1, and the release rule (docs/plans/fleet.md, "Invariants").
/// </summary>
public sealed class VehicleCommitmentRuleTests
{
    private static readonly Guid Mission = Guid.CreateVersion7();

    [Fact]
    public void Committing_an_available_vehicle_of_sufficient_capacity_sets_the_mission_and_raises_the_event()
    {
        var vehicle = TestVehicles.Registered(capacityKg: 1000m);

        Assert.True(vehicle.CommitToMission(Mission, 800m));

        Assert.Equal(Mission, vehicle.CommittedMissionId);
        Assert.Equal(new VehicleCommittedToMission(vehicle.Id, Mission), Assert.Single(vehicle.DomainEvents));
    }

    [Fact]
    public void An_Inactive_vehicle_breaks_VEHICLE_NOT_ACTIVE()
    {
        // Precondition 2.
        var vehicle = TestVehicles.Registered();
        vehicle.ChangeStatus(OperationalStatus.Inactive);
        vehicle.ClearEvents();

        var refused = Assert.Throws<BusinessRuleValidationException>(() => vehicle.CommitToMission(Mission, 1m));

        Assert.Equal("fleet", refused.Rule.ErrorDomain);
        Assert.Equal("VEHICLE_NOT_ACTIVE", refused.Rule.Code);
        Assert.Null(vehicle.CommittedMissionId);
        Assert.Empty(vehicle.DomainEvents);
    }

    [Fact]
    public void A_vehicle_under_maintenance_breaks_VEHICLE_UNDER_MAINTENANCE()
    {
        // Precondition 3. Section 8 of the challenge: a vehicle under maintenance must become unavailable.
        var vehicle = TestVehicles.Registered();
        vehicle.StartMaintenance();
        vehicle.ClearEvents();

        var refused = Assert.Throws<BusinessRuleValidationException>(() => vehicle.CommitToMission(Mission, 1m));

        Assert.Equal("VEHICLE_UNDER_MAINTENANCE", refused.Rule.Code);
        Assert.Null(vehicle.CommittedMissionId);
        Assert.Empty(vehicle.DomainEvents);
    }

    [Fact]
    public void A_vehicle_carrying_less_than_the_mission_requires_breaks_VEHICLE_CAPACITY_INSUFFICIENT()
    {
        // Precondition 4.
        var vehicle = TestVehicles.Registered(capacityKg: 999.99m);

        var refused = Assert.Throws<BusinessRuleValidationException>(() => vehicle.CommitToMission(Mission, 1000m));

        Assert.Equal("VEHICLE_CAPACITY_INSUFFICIENT", refused.Rule.Code);
        Assert.Null(vehicle.CommittedMissionId);
    }

    [Fact]
    public void Exactly_the_required_capacity_is_sufficient()
    {
        var vehicle = TestVehicles.Registered(capacityKg: 1000m);

        Assert.True(vehicle.CommitToMission(Mission, 1000m));
    }

    [Fact]
    public void A_vehicle_committed_to_another_mission_breaks_VEHICLE_NOT_AVAILABLE()
    {
        // Precondition 5.
        var vehicle = TestVehicles.Registered(capacityKg: 1000m);
        vehicle.CommitToMission(Mission, 100m);
        vehicle.ClearEvents();

        var refused = Assert.Throws<BusinessRuleValidationException>(
            () => vehicle.CommitToMission(Guid.CreateVersion7(), 100m));

        Assert.Equal("VEHICLE_NOT_AVAILABLE", refused.Rule.Code);
        Assert.Equal(Mission, vehicle.CommittedMissionId);
        Assert.Empty(vehicle.DomainEvents);
    }

    [Fact]
    public void Committing_to_the_mission_the_vehicle_already_holds_is_a_no_op_with_no_event()
    {
        // docs/plans/fleet.md: "CommitToMission for the mission the vehicle is already committed to is a
        // no-op (no event)."
        var vehicle = TestVehicles.Registered(capacityKg: 1000m);
        vehicle.CommitToMission(Mission, 100m);
        vehicle.ClearEvents();

        Assert.False(vehicle.CommitToMission(Mission, 100m));

        Assert.Equal(Mission, vehicle.CommittedMissionId);
        Assert.Empty(vehicle.DomainEvents);
    }

    [Fact]
    public void An_empty_mission_identity_is_refused()
    {
        var vehicle = TestVehicles.Registered();

        Assert.Throws<ArgumentException>(() => vehicle.CommitToMission(Guid.Empty, 1m));
    }

    [Fact]
    public void The_four_rules_are_checked_in_the_order_decision_1_lists_them()
    {
        // A vehicle that breaks several at once answers the first of them, so the order is observable and
        // is pinned rather than left to drift. Inactive and under maintenance and too small: NOT_ACTIVE.
        var vehicle = TestVehicles.Registered(capacityKg: 1m);
        vehicle.StartMaintenance();
        vehicle.ChangeStatus(OperationalStatus.Inactive);

        var refused = Assert.Throws<BusinessRuleValidationException>(() => vehicle.CommitToMission(Mission, 10_000m));
        Assert.Equal("VEHICLE_NOT_ACTIVE", refused.Rule.Code);

        // Active again, still under maintenance and still too small: UNDER_MAINTENANCE before CAPACITY.
        vehicle.ChangeStatus(OperationalStatus.Active);
        refused = Assert.Throws<BusinessRuleValidationException>(() => vehicle.CommitToMission(Mission, 10_000m));
        Assert.Equal("VEHICLE_UNDER_MAINTENANCE", refused.Rule.Code);
    }

    [Fact]
    public void A_committed_vehicle_is_no_longer_available()
    {
        // The one definition of "available" (L-9) and the commit rule now agree on one fact.
        var vehicle = TestVehicles.Registered(capacityKg: 1000m);
        vehicle.CommitToMission(Mission, 100m);

        Assert.False(VehicleAvailability.IsAvailable(vehicle));
    }

    [Fact]
    public void A_committed_vehicle_cannot_be_deactivated_or_sent_to_maintenance()
    {
        // F-3 and decision 3, now reachable through the production commitment path instead of a setter.
        var vehicle = TestVehicles.Registered(capacityKg: 1000m);
        vehicle.CommitToMission(Mission, 100m);

        Assert.Equal(
            "VEHICLE_HAS_MISSION_COMMITMENT",
            Assert.Throws<BusinessRuleValidationException>(() => vehicle.ChangeStatus(OperationalStatus.Inactive)).Rule.Code);
        Assert.Equal(
            "VEHICLE_HAS_MISSION_COMMITMENT",
            Assert.Throws<BusinessRuleValidationException>(vehicle.StartMaintenance).Rule.Code);
    }

    [Fact]
    public void Releasing_the_mission_that_holds_the_vehicle_clears_it_and_raises_the_event()
    {
        var vehicle = TestVehicles.Registered(capacityKg: 1000m);
        vehicle.CommitToMission(Mission, 100m);
        vehicle.ClearEvents();

        vehicle.ReleaseFromMission(Mission);

        Assert.Null(vehicle.CommittedMissionId);
        Assert.Equal(new VehicleReleasedFromMission(vehicle.Id, Mission), Assert.Single(vehicle.DomainEvents));
        Assert.True(VehicleAvailability.IsAvailable(vehicle));
    }

    [Fact]
    public void Releasing_an_uncommitted_vehicle_breaks_VEHICLE_NOT_COMMITTED_TO_MISSION()
    {
        var vehicle = TestVehicles.Registered();

        var refused = Assert.Throws<BusinessRuleValidationException>(() => vehicle.ReleaseFromMission(Mission));

        Assert.Equal("fleet", refused.Rule.ErrorDomain);
        Assert.Equal("VEHICLE_NOT_COMMITTED_TO_MISSION", refused.Rule.Code);
        Assert.Empty(vehicle.DomainEvents);
    }

    [Fact]
    public void One_mission_cannot_release_a_vehicle_another_mission_holds()
    {
        var vehicle = TestVehicles.Registered(capacityKg: 1000m);
        vehicle.CommitToMission(Mission, 100m);
        vehicle.ClearEvents();

        var refused = Assert.Throws<BusinessRuleValidationException>(
            () => vehicle.ReleaseFromMission(Guid.CreateVersion7()));

        Assert.Equal("VEHICLE_NOT_COMMITTED_TO_MISSION", refused.Rule.Code);
        Assert.Equal(Mission, vehicle.CommittedMissionId);
    }
}

/// <summary>
/// That the two commitment events are now actually <em>raised</em> by the aggregate, not only declared and
/// handled. <c>CacheEvictionRoutingTests</c> proves the routing; this proves there is something to route.
/// </summary>
public sealed class VehicleCommitmentEventTests
{
    [Fact]
    public void The_two_commitment_events_have_an_aggregate_method_that_raises_each()
    {
        var mission = Guid.CreateVersion7();
        var vehicle = TestVehicles.Registered(capacityKg: 1000m);

        vehicle.CommitToMission(mission, 100m);
        vehicle.ReleaseFromMission(mission);

        Assert.Equal(
            [
                new VehicleCommittedToMission(vehicle.Id, mission),
                new VehicleReleasedFromMission(vehicle.Id, mission),
            ],
            vehicle.DomainEvents);
    }

    [Fact]
    public void Every_domain_event_of_the_module_is_now_raised_by_some_aggregate_method()
    {
        // Until this round, VehicleCommittedToMission and VehicleReleasedFromMission were declared and
        // handled but raised by nothing (L-10). All six are now produced by Vehicle itself.
        var mission = Guid.CreateVersion7();
        var vehicle = Vehicle.Register(Guid.CreateVersion7(), PlateNumber.Create("AB-1"), VehicleType.Truck, Capacity.FromKilograms(1000m));
        vehicle.ChangeStatus(OperationalStatus.Inactive);
        vehicle.StartMaintenance();
        vehicle.CompleteMaintenance();
        vehicle.ChangeStatus(OperationalStatus.Active);
        vehicle.CommitToMission(mission, 1m);
        vehicle.ReleaseFromMission(mission);

        var raised = vehicle.DomainEvents.Select(domainEvent => domainEvent.GetType().Name).Distinct().Order();
        var declared = Fserp.FleetOperations.Modules.Fleet.AssemblyReference.Assembly.GetTypes()
            .Where(type => typeof(MPCore.Domain.Events.IDomainEvent).IsAssignableFrom(type) && !type.IsAbstract)
            .Select(type => type.Name)
            .Order();

        Assert.Equal(declared, raised);
    }
}

/// <summary>A read model that answers one snapshot and counts how often it was asked.</summary>
internal sealed class FakeSnapshotReadModel(VehicleSnapshot? snapshot = null) : IVehicleReadModel
{
    public int SnapshotReads { get; private set; }

    public Guid? AskedFor { get; private set; }

    public Task<VehicleView?> GetAsync(Guid vehicleId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This fake serves the snapshot only.");

    public Task<IReadOnlyList<AvailableVehicleView>> GetAvailableAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException("This fake serves the snapshot only.");

    public Task<VehicleSnapshot?> GetSnapshotAsync(Guid vehicleId, CancellationToken cancellationToken)
    {
        AskedFor = vehicleId;
        SnapshotReads++;
        return Task.FromResult(snapshot);
    }
}

/// <summary>
/// <c>VehicleAvailabilityReader</c> (docs/architecture.md, decisions 1 and 4): the snapshot is read from
/// PostgreSQL, never from the cache.
/// </summary>
public sealed class VehicleAvailabilityReaderTests
{
    private static readonly VehicleSnapshot Snapshot =
        new(Guid.CreateVersion7(), VehicleType.Truck, 1200m, true, false, null);

    [Fact]
    public async Task Returns_the_snapshot_the_read_model_answers()
    {
        var readModel = new FakeSnapshotReadModel(Snapshot);

        var result = await new VehicleAvailabilityReader(readModel).GetAsync(Snapshot.VehicleId, CancellationToken.None);

        Assert.Same(Snapshot, result);
        Assert.Equal(Snapshot.VehicleId, readModel.AskedFor);
    }

    [Fact]
    public async Task An_unknown_vehicle_is_null_rather_than_a_failure()
    {
        var result = await new VehicleAvailabilityReader(new FakeSnapshotReadModel())
            .GetAsync(Guid.CreateVersion7(), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task Every_call_reads_the_database_so_the_snapshot_is_never_served_from_the_cache()
    {
        // Decision 4 lists IVehicleAvailabilityReader under "Never cached", because Assign decides on the
        // answer while the cached available list is advisory and may be up to 30 s stale.
        var readModel = new FakeSnapshotReadModel(Snapshot);
        var reader = new VehicleAvailabilityReader(readModel);

        await reader.GetAsync(Snapshot.VehicleId, CancellationToken.None);
        await reader.GetAsync(Snapshot.VehicleId, CancellationToken.None);

        Assert.Equal(2, readModel.SnapshotReads);
    }

    [Fact]
    public void The_reader_takes_the_read_port_and_no_cache_port()
    {
        // Stronger than counting reads: it has no cache to read from. GetAvailableVehiclesHandler, which
        // does cache, declares IReadThroughCache in exactly this position.
        var parameters = typeof(VehicleAvailabilityReader).GetConstructors().Single().GetParameters();

        Assert.Equal(typeof(IVehicleReadModel), Assert.Single(parameters).ParameterType);
        Assert.DoesNotContain(parameters, parameter =>
            parameter.ParameterType.Name.Contains("Cache", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// <c>VehicleCommitments</c> (docs/architecture.md, decision 1): it loads through <c>IVehicleRepository</c>,
/// calls the aggregate method and does not save.
/// </summary>
public sealed class VehicleCommitmentsTests
{
    private readonly FakeVehicleRepository _vehicles = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly Guid _mission = Guid.CreateVersion7();

    private VehicleCommitments Port => new(_vehicles);

    private Vehicle Stored(decimal capacityKg = 1000m)
    {
        var vehicle = TestVehicles.Registered("AB-" + Guid.NewGuid().ToString("N")[..6], capacityKg);
        _vehicles.Stored[vehicle.Id] = vehicle;
        return vehicle;
    }

    [Fact]
    public async Task Committing_loads_the_aggregate_and_sets_the_mission()
    {
        var vehicle = Stored();

        await Port.CommitToMissionAsync(vehicle.Id, _mission, 500m, CancellationToken.None);

        Assert.Equal(_mission, vehicle.CommittedMissionId);
        Assert.Equal(new VehicleCommittedToMission(vehicle.Id, _mission), Assert.Single(vehicle.DomainEvents));
    }

    [Fact]
    public async Task Committing_does_not_save_so_the_callers_unit_of_work_commits()
    {
        var vehicle = Stored();

        await Port.CommitToMissionAsync(vehicle.Id, _mission, 500m, CancellationToken.None);

        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public void The_port_declares_no_unit_of_work_at_all()
    {
        var parameters = typeof(VehicleCommitments).GetConstructors().Single().GetParameters();

        Assert.Equal(typeof(IVehicleRepository), Assert.Single(parameters).ParameterType);
        Assert.DoesNotContain(parameters, parameter => parameter.ParameterType == typeof(IUnitOfWork));
        Assert.DoesNotContain(
            typeof(VehicleCommitments).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
            method => method.Name.Contains("Save", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_broken_rule_leaves_the_port_as_the_rule_under_the_fleet_domain()
    {
        var vehicle = Stored(capacityKg: 100m);

        var refused = await Assert.ThrowsAsync<BusinessRuleValidationException>(() =>
            Port.CommitToMissionAsync(vehicle.Id, _mission, 5000m, CancellationToken.None));

        Assert.Equal("fleet", refused.Rule.ErrorDomain);
        Assert.Equal("VEHICLE_CAPACITY_INSUFFICIENT", refused.Rule.Code);
        Assert.Null(vehicle.CommittedMissionId);
    }

    [Fact]
    public async Task An_unknown_vehicle_is_the_modules_own_not_found_failure()
    {
        var refused = await Assert.ThrowsAsync<ResultFailureException>(() =>
            Port.CommitToMissionAsync(Guid.CreateVersion7(), _mission, 1m, CancellationToken.None));

        Assert.Equal(new ErrorIdentity("fleet", "VEHICLE_NOT_FOUND"), refused.Failure.Identity);
        Assert.Equal(ErrorCategory.NotFound, refused.Failure.Category);
        Assert.Equal(FleetFailures.VehicleNotFound().Message.Key, refused.Failure.Message.Key);
    }

    [Fact]
    public async Task Releasing_clears_the_mission_and_does_not_save()
    {
        var vehicle = Stored();
        await Port.CommitToMissionAsync(vehicle.Id, _mission, 1m, CancellationToken.None);
        vehicle.ClearEvents();

        await Port.ReleaseFromMissionAsync(vehicle.Id, _mission, CancellationToken.None);

        Assert.Null(vehicle.CommittedMissionId);
        Assert.Equal(new VehicleReleasedFromMission(vehicle.Id, _mission), Assert.Single(vehicle.DomainEvents));
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task Releasing_an_unknown_vehicle_is_the_modules_own_not_found_failure()
    {
        var refused = await Assert.ThrowsAsync<ResultFailureException>(() =>
            Port.ReleaseFromMissionAsync(Guid.CreateVersion7(), _mission, CancellationToken.None));

        Assert.Equal(new ErrorIdentity("fleet", "VEHICLE_NOT_FOUND"), refused.Failure.Identity);
    }
}
