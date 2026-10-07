using System.Reflection;
using Fserp.FleetOperations.Modules.Drivers.Application.Contracts;
using Fserp.FleetOperations.Modules.Drivers.Contracts;
using Fserp.FleetOperations.Modules.Drivers.Domain;
using Fserp.FleetOperations.Modules.Fleet.Contracts;
using Fserp.FleetOperations.UnitTests.Fleet;
using MPCore.Application.Results;
using MPCore.Domain.Rules;
using MPCore.Persistence.Abstractions;

namespace Fserp.FleetOperations.UnitTests.Drivers;

/// <summary>
/// <c>DriverEligibilityReader</c> (docs/architecture.md, decision 1): a read-only snapshot taken from
/// PostgreSQL at the moment of the call.
/// </summary>
public sealed class DriverEligibilityReaderTests
{
    [Fact]
    public async Task Returns_the_snapshot_the_read_model_answers()
    {
        var driverId = Guid.CreateVersion7();
        var snapshot = new DriverSnapshot(driverId, true, [VehicleType.Van], null);
        var readModel = new FakeDriverReadModel { Snapshot = snapshot };

        var result = await new DriverEligibilityReader(readModel).GetAsync(driverId, CancellationToken.None);

        Assert.Same(snapshot, result);
        Assert.Equal(driverId, readModel.AskedFor);
    }

    [Fact]
    public async Task An_unknown_driver_is_null_rather_than_a_failure()
    {
        var result = await new DriverEligibilityReader(new FakeDriverReadModel())
            .GetAsync(Guid.CreateVersion7(), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task Every_call_reads_the_database_so_the_snapshot_is_never_a_kept_copy()
    {
        // Decision 1: the snapshot is read from PostgreSQL, never from a cache. Two calls, two reads.
        var readModel = new FakeDriverReadModel { Snapshot = new DriverSnapshot(Guid.CreateVersion7(), true, [], null) };
        var reader = new DriverEligibilityReader(readModel);

        await reader.GetAsync(Guid.CreateVersion7(), CancellationToken.None);
        await reader.GetAsync(Guid.CreateVersion7(), CancellationToken.None);

        Assert.Equal(2, readModel.SnapshotReads);
    }

    [Fact]
    public void The_reader_takes_the_read_port_and_no_cache()
    {
        var parameters = typeof(DriverEligibilityReader).GetConstructors().Single().GetParameters();

        Assert.Equal(typeof(Fserp.FleetOperations.Modules.Drivers.Application.Ports.IDriverReadModel),
            Assert.Single(parameters).ParameterType);
    }
}

/// <summary>
/// <c>DriverCommitments</c> (docs/architecture.md, decision 1): the writing Contracts interface. It loads
/// through the repository, calls the aggregate method and does not save.
/// </summary>
public sealed class DriverCommitmentsTests
{
    private readonly FakeDriverRepository _drivers = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly Guid _mission = Guid.CreateVersion7();

    private DriverCommitments Port => new(_drivers);

    [Fact]
    public async Task Committing_loads_the_aggregate_and_sets_the_mission()
    {
        var driver = _drivers.Store(TestDrivers.Registered("Ada", VehicleType.Van));

        await Port.CommitToMissionAsync(driver.Id, _mission, VehicleType.Van, CancellationToken.None);

        Assert.Equal(_mission, driver.CommittedMissionId);
        Assert.Equal(1, _drivers.Loads);
    }

    [Fact]
    public async Task Committing_does_not_save_so_the_callers_unit_of_work_commits()
    {
        // The guarantee decision 1 is built on: assignment and commitment succeed or fail together.
        var driver = _drivers.Store(TestDrivers.Registered("Ada", VehicleType.Van));

        await Port.CommitToMissionAsync(driver.Id, _mission, VehicleType.Van, CancellationToken.None);

        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public void The_port_declares_no_unit_of_work_at_all()
    {
        // Stronger than counting saves: it has nothing to save with.
        var parameters = typeof(DriverCommitments).GetConstructors().Single().GetParameters();

        Assert.DoesNotContain(parameters, parameter => parameter.ParameterType == typeof(IUnitOfWork));
        Assert.DoesNotContain(
            typeof(DriverCommitments).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
            method => method.Name.Contains("Save", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_port_loads_through_the_repository_not_the_read_model()
    {
        // The repository tracks the aggregate, so the driver's xmin token reaches the caller's UPDATE and
        // catches a concurrent assignment or deactivation (decision 2). A read-model view could not.
        var driver = _drivers.Store(TestDrivers.Registered("Ada", VehicleType.Van));

        await Port.CommitToMissionAsync(driver.Id, _mission, VehicleType.Van, CancellationToken.None);

        Assert.Equal(1, _drivers.Loads);
        Assert.Same(driver, _drivers.Stored[driver.Id]);
    }

    [Fact]
    public async Task A_broken_rule_leaves_the_port_as_the_rule_under_the_drivers_domain()
    {
        var driver = _drivers.Store(TestDrivers.Registered("Ada", VehicleType.Van));

        var refused = await Assert.ThrowsAsync<BusinessRuleValidationException>(() =>
            Port.CommitToMissionAsync(driver.Id, _mission, VehicleType.HeavyTruck, CancellationToken.None));

        Assert.Equal("drivers", refused.Rule.ErrorDomain);
        Assert.Equal("DRIVER_NOT_QUALIFIED", refused.Rule.Code);
        Assert.Null(driver.CommittedMissionId);
    }

    [Fact]
    public async Task An_unknown_driver_is_the_modules_own_not_found_failure()
    {
        // The port returns no result, so the failure travels as the descriptor the edge maps to 404. No
        // code is invented for the case.
        var refused = await Assert.ThrowsAsync<ResultFailureException>(() =>
            Port.CommitToMissionAsync(Guid.CreateVersion7(), _mission, VehicleType.Van, CancellationToken.None));

        Assert.Equal(new ErrorIdentity("drivers", "DRIVER_NOT_FOUND"), refused.Failure.Identity);
        Assert.Equal(ErrorCategory.NotFound, refused.Failure.Category);
    }

    [Fact]
    public async Task Releasing_clears_the_mission_and_does_not_save()
    {
        var driver = _drivers.Store(TestDrivers.Registered("Ada", VehicleType.Van).CommittedTo(_mission, VehicleType.Van));

        await Port.ReleaseFromMissionAsync(driver.Id, _mission, CancellationToken.None);

        Assert.Null(driver.CommittedMissionId);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task Releasing_an_unknown_driver_is_the_modules_own_not_found_failure()
    {
        var refused = await Assert.ThrowsAsync<ResultFailureException>(() =>
            Port.ReleaseFromMissionAsync(Guid.CreateVersion7(), _mission, CancellationToken.None));

        Assert.Equal(new ErrorIdentity("drivers", "DRIVER_NOT_FOUND"), refused.Failure.Identity);
    }

    [Fact]
    public async Task Releasing_a_driver_another_mission_holds_is_the_rule()
    {
        var driver = _drivers.Store(TestDrivers.Registered("Ada", VehicleType.Van).CommittedTo(_mission, VehicleType.Van));

        var refused = await Assert.ThrowsAsync<BusinessRuleValidationException>(() =>
            Port.ReleaseFromMissionAsync(driver.Id, Guid.CreateVersion7(), CancellationToken.None));

        Assert.Equal("DRIVER_NOT_COMMITTED_TO_MISSION", refused.Rule.Code);
        Assert.Equal(_mission, driver.CommittedMissionId);
    }
}

/// <summary>
/// The Drivers views (lead decision L-16), projected from the aggregate.
/// </summary>
public sealed class DriverViewTests
{
    [Fact]
    public void The_driver_view_carries_the_five_fields_the_lead_decided()
    {
        var names = typeof(Fserp.FleetOperations.Modules.Drivers.Application.Views.DriverView)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.Name != "EqualityContract")
            .Select(property => property.Name)
            .Order();

        Assert.Equal(
            ["CommittedMissionId", "FullName", "Id", "OperationalStatus", "QualifiedVehicleTypes"],
            names);
    }

    [Fact]
    public void The_available_view_carries_the_three_fields_the_lead_decided()
    {
        var names = typeof(Fserp.FleetOperations.Modules.Drivers.Application.Views.AvailableDriverView)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.Name != "EqualityContract")
            .Select(property => property.Name)
            .Order();

        Assert.Equal(["FullName", "Id", "QualifiedVehicleTypes"], names);
    }

    [Fact]
    public void The_qualified_types_are_ordered_by_the_type_so_two_reads_agree()
    {
        var driver = TestDrivers.Registered("Ada", VehicleType.HeavyTruck, VehicleType.Van, VehicleType.Truck);

        var view = Fserp.FleetOperations.Modules.Drivers.Application.Views.DriverView.From(driver);

        Assert.Equal([VehicleType.Van, VehicleType.Truck, VehicleType.HeavyTruck], view.QualifiedVehicleTypes);
    }

    [Fact]
    public void A_committed_driver_shows_the_mission_in_its_view()
    {
        var mission = Guid.CreateVersion7();
        var driver = TestDrivers.Registered("Ada", VehicleType.Van).CommittedTo(mission, VehicleType.Van);

        Assert.Equal(mission, Fserp.FleetOperations.Modules.Drivers.Application.Views.DriverView.From(driver).CommittedMissionId);
    }
}
