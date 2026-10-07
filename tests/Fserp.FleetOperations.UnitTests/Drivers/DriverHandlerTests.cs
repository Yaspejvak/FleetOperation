using Fserp.FleetOperations.Modules.Drivers.Application.Commands;
using Fserp.FleetOperations.Modules.Drivers.Application.Ports;
using Fserp.FleetOperations.Modules.Drivers.Application.Queries;
using Fserp.FleetOperations.Modules.Drivers.Application.Views;
using Fserp.FleetOperations.Modules.Drivers.Domain;
using Fserp.FleetOperations.Modules.Drivers.Domain.Events;
using Fserp.FleetOperations.Modules.Fleet.Contracts;
using Fserp.FleetOperations.UnitTests.Fleet;
using MPCore.Application.Results;
using MPCore.Audit;
using MPCore.Domain.Rules;
using MPCore.Persistence.Abstractions;

namespace Fserp.FleetOperations.UnitTests.Drivers;

public sealed class RegisterDriverHandlerTests
{
    private readonly FakeDriverRepository _drivers = new();
    private readonly FakeAuditRecorder _audit = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FixedClock _clock = new(TestDrivers.Now);

    private Task<Result<DriverView>> Register(string fullName, params VehicleType[] types) =>
        RegisterDriverHandler.Handle(
            new RegisterDriver(fullName, types), _drivers, _unitOfWork, _clock, _audit, CancellationToken.None);

    [Fact]
    public async Task Registers_an_Active_driver_and_returns_its_view()
    {
        var result = await Register("  Ada Lovelace  ", VehicleType.Van, VehicleType.HeavyTruck);

        Assert.True(result.IsSuccess);
        var added = Assert.Single(_drivers.Added);
        Assert.Equal(added.Id, result.Value.Id);
        Assert.Equal("Ada Lovelace", result.Value.FullName);
        Assert.Equal(OperationalStatus.Active, result.Value.OperationalStatus);
        Assert.Null(result.Value.CommittedMissionId);
        // L-16 and DriverView.From: the types are ordered by the type, not by the order they were sent.
        Assert.Equal([VehicleType.Van, VehicleType.HeavyTruck], result.Value.QualifiedVehicleTypes);
    }

    [Fact]
    public async Task The_identity_is_a_version_7_uuid_timestamped_by_the_clock()
    {
        // docs/plans/README.md: time comes only from IClock.
        var result = await Register("Ada", VehicleType.Van);

        Assert.Equal(7, result.Value.Id.Version);
        var bytes = result.Value.Id.ToByteArray(bigEndian: true);
        var milliseconds = ((long)bytes[0] << 40) | ((long)bytes[1] << 32) | ((long)bytes[2] << 24)
            | ((long)bytes[3] << 16) | ((long)bytes[4] << 8) | bytes[5];
        Assert.Equal(TestDrivers.Now.ToUnixTimeMilliseconds(), milliseconds);
    }

    [Fact]
    public async Task Records_DriverRegistered_for_the_new_driver_and_no_attempt()
    {
        var result = await Register("Ada", VehicleType.Van);

        var recorded = Assert.Single(_audit.Actions);
        Assert.Equal(new RecordedAction("Drivers", "DriverRegistered", "Driver", result.Value.Id.ToString(), null), recorded);
        Assert.Empty(_audit.Attempts);
    }

    [Fact]
    public async Task The_audit_action_carries_no_metadata_so_the_name_cannot_reach_it_unmasked()
    {
        // The name is personal data. The policy masks the column; metadata values are not masked at all,
        // so the handler puts none there.
        await Register("Ada Lovelace", VehicleType.Van);

        Assert.Null(Assert.Single(_audit.Actions).Metadata);
    }

    [Fact]
    public async Task Raises_DriverRegistered_on_the_added_aggregate_for_delivery_after_commit()
    {
        var result = await Register("Ada", VehicleType.Van);

        var added = Assert.Single(_drivers.Added);
        Assert.Equal(new DriverRegistered(result.Value.Id), Assert.Single(added.DomainEvents));
    }

    [Fact]
    public async Task Does_not_save_itself()
    {
        await Register("Ada", VehicleType.Van);

        // The middleware owns the transaction.
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_name_that_bypassed_the_validator_is_the_name_rule_and_changes_nothing(string name)
    {
        var refused = await Assert.ThrowsAsync<BusinessRuleValidationException>(() => Register(name, VehicleType.Van));

        Assert.Equal("drivers", refused.Rule.ErrorDomain);
        Assert.Equal("DRIVER_NAME_REQUIRED", refused.Rule.Code);
        Assert.Empty(_drivers.Added);
        Assert.Empty(_audit.Actions);
        Assert.Empty(_audit.Attempts);
    }

    [Fact]
    public async Task No_qualification_that_bypassed_the_validator_is_the_qualification_rule_and_changes_nothing()
    {
        var refused = await Assert.ThrowsAsync<BusinessRuleValidationException>(() => Register("Ada"));

        Assert.Equal("DRIVER_QUALIFICATION_REQUIRED", refused.Rule.Code);
        Assert.Empty(_drivers.Added);
        Assert.Empty(_audit.Actions);
    }

    [Fact]
    public async Task A_duplicate_qualification_that_bypassed_the_validator_is_the_duplicate_rule()
    {
        var refused = await Assert.ThrowsAsync<BusinessRuleValidationException>(
            () => Register("Ada", VehicleType.Van, VehicleType.Van));

        Assert.Equal("DRIVER_QUALIFICATION_DUPLICATE", refused.Rule.Code);
        Assert.Empty(_drivers.Added);
    }

    [Fact]
    public async Task A_null_vehicle_type_list_is_the_qualification_rule_rather_than_a_null_reference()
    {
        // The endpoint already substitutes an empty list; the handler does not depend on that.
        var refused = await Assert.ThrowsAsync<BusinessRuleValidationException>(() =>
            RegisterDriverHandler.Handle(
                new RegisterDriver("Ada", null!), _drivers, _unitOfWork, _clock, _audit, CancellationToken.None));

        Assert.Equal("DRIVER_QUALIFICATION_REQUIRED", refused.Rule.Code);
    }
}

public sealed class ChangeDriverStatusHandlerTests
{
    private readonly FakeDriverRepository _drivers = new();
    private readonly FakeAuditRecorder _audit = new();
    private readonly FakeUnitOfWork _unitOfWork = new();

    private Task<Result<DriverView>> Change(Guid driverId, OperationalStatus status) =>
        ChangeDriverStatusHandler.Handle(
            new ChangeDriverStatus(driverId, status), _drivers, _unitOfWork, _audit, CancellationToken.None);

    [Fact]
    public async Task An_unknown_driver_is_a_404()
    {
        var result = await Change(Guid.CreateVersion7(), OperationalStatus.Inactive);

        Assert.True(result.IsFailure);
        Assert.Equal(new ErrorIdentity("drivers", "DRIVER_NOT_FOUND"), result.FailureDescriptor!.Identity);
        Assert.Equal(ErrorCategory.NotFound, result.FailureDescriptor.Category);
        Assert.Equal("drivers.driver_not_found", result.FailureDescriptor.Message.Key);
        Assert.Empty(_audit.Actions);
        Assert.Empty(_audit.Attempts);
    }

    [Fact]
    public async Task Changes_the_status_and_records_DriverStatusChanged_with_from_and_to()
    {
        var driver = _drivers.Store(TestDrivers.Registered());

        var result = await Change(driver.Id, OperationalStatus.Inactive);

        Assert.True(result.IsSuccess);
        Assert.Equal(OperationalStatus.Inactive, result.Value.OperationalStatus);
        var recorded = Assert.Single(_audit.Actions);
        Assert.Equal("Drivers", recorded.Module);
        Assert.Equal("DriverStatusChanged", recorded.Action);
        Assert.Equal("Driver", recorded.EntityType);
        Assert.Equal(driver.Id.ToString(), recorded.EntityId);
        Assert.Equal("Active", recorded.Metadata!["from"]);
        Assert.Equal("Inactive", recorded.Metadata["to"]);
        Assert.Empty(_audit.Attempts);
    }

    [Fact]
    public async Task Reactivation_is_recorded_with_from_Inactive_to_Active()
    {
        var driver = TestDrivers.Registered();
        driver.ChangeStatus(OperationalStatus.Inactive);
        driver.ClearEvents();
        _drivers.Store(driver);

        var result = await Change(driver.Id, OperationalStatus.Active);

        Assert.True(result.IsSuccess);
        var recorded = Assert.Single(_audit.Actions);
        Assert.Equal("Inactive", recorded.Metadata!["from"]);
        Assert.Equal("Active", recorded.Metadata["to"]);
        Assert.Equal(
            new DriverStatusChanged(driver.Id, OperationalStatus.Inactive, OperationalStatus.Active),
            Assert.Single(driver.DomainEvents));
    }

    [Fact]
    public async Task The_current_status_is_a_200_without_event_or_audit()
    {
        var driver = _drivers.Store(TestDrivers.Registered());

        var result = await Change(driver.Id, OperationalStatus.Active);

        Assert.True(result.IsSuccess);
        Assert.Equal(OperationalStatus.Active, result.Value.OperationalStatus);
        Assert.Empty(driver.DomainEvents);
        Assert.Empty(_audit.Actions);
        Assert.Empty(_audit.Attempts);
    }

    [Fact]
    public async Task A_committed_driver_set_Inactive_is_refused_and_the_attempt_is_recorded()
    {
        // D-2; the refusal is audited as a rejected attempt (docs/plans/drivers.md, "Business audit").
        var driver = _drivers.Store(TestDrivers.Registered().CommittedTo(Guid.CreateVersion7()));

        var refused = await Assert.ThrowsAsync<BusinessRuleValidationException>(
            () => Change(driver.Id, OperationalStatus.Inactive));

        Assert.Equal("DRIVER_HAS_MISSION_COMMITMENT", refused.Rule.Code);
        Assert.Equal(OperationalStatus.Active, driver.OperationalStatus);
        Assert.Empty(_audit.Actions);
        var attempt = Assert.Single(_audit.Attempts);
        Assert.Equal("Drivers", attempt.Module);
        Assert.Equal("DriverStatusChanged", attempt.Action);
        Assert.Equal(AuditOutcome.Rejected, attempt.Outcome);
        Assert.Equal(new AuditFailure("drivers", "DRIVER_HAS_MISSION_COMMITMENT"), attempt.Failure);
        Assert.Equal("Driver", attempt.EntityType);
        Assert.Equal(driver.Id.ToString(), attempt.EntityId);
        Assert.Equal("Active", attempt.Metadata!["from"]);
        Assert.Equal("Inactive", attempt.Metadata["to"]);
    }

    [Fact]
    public async Task The_rejected_attempt_names_the_driver_and_the_requested_change_and_nothing_else()
    {
        // The driver's name must never reach the trail unmasked, and metadata is not masked.
        var driver = _drivers.Store(TestDrivers.Registered("Ada Lovelace").CommittedTo(Guid.CreateVersion7()));

        await Assert.ThrowsAsync<BusinessRuleValidationException>(() => Change(driver.Id, OperationalStatus.Inactive));

        var attempt = Assert.Single(_audit.Attempts);
        Assert.Equal(["from", "to"], attempt.Metadata!.Keys.Order());
        Assert.DoesNotContain("Ada", string.Join('|', attempt.Metadata.Values), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_no_op_on_a_committed_driver_records_no_attempt()
    {
        var driver = _drivers.Store(TestDrivers.Registered().CommittedTo(Guid.CreateVersion7()));

        var result = await Change(driver.Id, OperationalStatus.Active);

        Assert.True(result.IsSuccess);
        Assert.Empty(_audit.Actions);
        Assert.Empty(_audit.Attempts);
        Assert.Empty(driver.DomainEvents);
    }

    [Fact]
    public async Task Change_status_never_saves_itself_on_any_path()
    {
        var plain = _drivers.Store(TestDrivers.Registered("Ada"));
        var committed = _drivers.Store(TestDrivers.Registered("Grace").CommittedTo(Guid.CreateVersion7()));

        await Change(plain.Id, OperationalStatus.Inactive);
        await Change(plain.Id, OperationalStatus.Inactive);
        await Change(Guid.CreateVersion7(), OperationalStatus.Active);
        await Assert.ThrowsAsync<BusinessRuleValidationException>(() => Change(committed.Id, OperationalStatus.Inactive));

        Assert.Equal(0, _unitOfWork.Saves);
    }
}

public sealed class GetDriverHandlerTests
{
    [Fact]
    public async Task Returns_the_view()
    {
        var view = DriverView.From(TestDrivers.Registered("Ada", VehicleType.Van));
        var readModel = new FakeDriverReadModel { View = view };

        var result = await GetDriverHandler.Handle(new GetDriver(view.Id), readModel, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Same(view, result.Value);
        Assert.Equal(view.Id, readModel.AskedFor);
    }

    [Fact]
    public async Task An_unknown_driver_is_a_404()
    {
        var result = await GetDriverHandler.Handle(
            new GetDriver(Guid.CreateVersion7()), new FakeDriverReadModel(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(new ErrorIdentity("drivers", "DRIVER_NOT_FOUND"), result.FailureDescriptor!.Identity);
        Assert.Equal(ErrorCategory.NotFound, result.FailureDescriptor.Category);
    }

    [Fact]
    public void The_query_handler_declares_no_unit_of_work_and_no_repository()
    {
        var parameters = typeof(GetDriverHandler).GetMethod(nameof(GetDriverHandler.Handle))!
            .GetParameters().Select(parameter => parameter.ParameterType).ToList();

        Assert.DoesNotContain(typeof(IUnitOfWork), parameters);
        Assert.DoesNotContain(typeof(IDriverRepository), parameters);
    }
}

public sealed class GetAvailableDriversHandlerTests
{
    private static readonly AvailableDriverView One =
        new(Guid.CreateVersion7(), "Ada Lovelace", [VehicleType.Van]);

    [Fact]
    public async Task Returns_the_list_the_read_model_answers()
    {
        var readModel = new FakeDriverReadModel();
        readModel.Available.Add(One);

        var result = await GetAvailableDriversHandler.Handle(
            new GetAvailableDrivers(), readModel, CancellationToken.None);

        Assert.Equal([One], result);
        Assert.Equal(1, readModel.AvailableReads);
    }

    [Fact]
    public async Task An_empty_set_is_an_empty_list_and_not_a_failure()
    {
        // L-14's reasoning: the query returns the list itself, so there is no failure case to map.
        var result = await GetAvailableDriversHandler.Handle(
            new GetAvailableDrivers(), new FakeDriverReadModel(), CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task Every_call_reads_the_database_because_nothing_is_cached()
    {
        // D-6 and the plan's query table ("Cached: no"). Two calls, two reads: no entry is kept between
        // them, so a driver registered in between is visible at once.
        var readModel = new FakeDriverReadModel();

        await GetAvailableDriversHandler.Handle(new GetAvailableDrivers(), readModel, CancellationToken.None);
        await GetAvailableDriversHandler.Handle(new GetAvailableDrivers(), readModel, CancellationToken.None);

        Assert.Equal(2, readModel.AvailableReads);
    }

    [Fact]
    public void The_query_carries_no_filter_and_no_page()
    {
        // D-6: Get Available Drivers has no vehicle-type filter.
        Assert.Empty(typeof(GetAvailableDrivers).GetProperties(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance));
    }
}
