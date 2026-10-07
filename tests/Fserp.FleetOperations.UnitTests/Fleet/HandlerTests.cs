using Fserp.FleetOperations.Modules.Fleet.Application.Commands;
using Fserp.FleetOperations.Modules.Fleet.Application.Ports;
using Fserp.FleetOperations.Modules.Fleet.Application.Queries;
using Fserp.FleetOperations.Modules.Fleet.Application.Views;
using Fserp.FleetOperations.Modules.Fleet.Contracts;
using Fserp.FleetOperations.Modules.Fleet.Domain;
using MPCore.Application.Results;
using MPCore.Audit;
using MPCore.Domain.Rules;

namespace Fserp.FleetOperations.UnitTests.Fleet;

public sealed class RegisterVehicleHandlerTests
{
    private readonly FakeVehicleRepository _vehicles = new();
    private readonly FakeAuditRecorder _audit = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero));

    private Task<Result<VehicleView>> Register(string plate, VehicleType type = VehicleType.Truck, decimal capacityKg = 1200m) =>
        RegisterVehicleHandler.Handle(new RegisterVehicle(plate, type, capacityKg), _vehicles, _unitOfWork, _clock, _audit, CancellationToken.None);

    [Fact]
    public async Task Registers_an_Active_vehicle_and_returns_its_view()
    {
        var result = await Register(" ab-77 ", VehicleType.Van, 950.25m);

        Assert.True(result.IsSuccess);
        var added = Assert.Single(_vehicles.Added);
        Assert.Equal(new VehicleView(
            added.Id, "AB-77", VehicleType.Van, 950.25m, OperationalStatus.Active,
            MaintenanceStatus.NotUnderMaintenance, VehicleDisplayStatus.Active, null), result.Value);
    }

    [Fact]
    public async Task The_identity_is_a_version_7_uuid()
    {
        var result = await Register("AB-1");

        Assert.Equal(7, result.Value.Id.Version);
    }

    [Fact]
    public async Task Records_VehicleRegistered_for_the_new_vehicle()
    {
        var result = await Register("AB-1");

        var recorded = Assert.Single(_audit.Actions);
        Assert.Equal(new RecordedAction("Fleet", "VehicleRegistered", "Vehicle", result.Value.Id.ToString(), null), recorded);
        Assert.Empty(_audit.Attempts);
    }

    [Fact]
    public async Task Does_not_save_itself()
    {
        await Register("AB-1");

        // The middleware owns the transaction.
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Theory]
    [InlineData("AB-1")]
    [InlineData(" ab-1 ")]
    public async Task A_plate_already_registered_is_a_409_and_changes_nothing(string sameplate)
    {
        await Register("AB-1");
        _audit.Actions.Clear();

        var result = await Register(sameplate);

        Assert.True(result.IsFailure);
        Assert.Equal(new ErrorIdentity("fleet", "VEHICLE_PLATE_NUMBER_ALREADY_REGISTERED"), result.FailureDescriptor!.Identity);
        Assert.Equal(ErrorCategory.AlreadyExists, result.FailureDescriptor.Category);
        Assert.Equal("fleet.vehicle_plate_number_already_registered", result.FailureDescriptor.Message.Key);
        Assert.Single(_vehicles.Added);
        Assert.Empty(_audit.Actions);
    }

    [Fact]
    public async Task A_broken_value_rule_reaches_the_caller_as_the_rule()
    {
        // The validator normally refuses this first; the value object is the enforcement regardless.
        var refused = await Assert.ThrowsAsync<BusinessRuleValidationException>(() => Register("AB-1", capacityKg: 0m));

        Assert.Equal("VEHICLE_CAPACITY_MUST_BE_POSITIVE", refused.Rule.Code);
        Assert.Empty(_vehicles.Added);
        Assert.Empty(_audit.Actions);
    }
}

public sealed class ChangeVehicleStatusHandlerTests
{
    private readonly FakeVehicleRepository _vehicles = new();
    private readonly FakeAuditRecorder _audit = new();
    private readonly FakeUnitOfWork _unitOfWork = new();

    private Vehicle Stored(Vehicle vehicle)
    {
        _vehicles.Stored[vehicle.Id] = vehicle;
        return vehicle;
    }

    private Task<Result<VehicleView>> Change(Guid vehicleId, OperationalStatus status) =>
        ChangeVehicleStatusHandler.Handle(new ChangeVehicleStatus(vehicleId, status), _vehicles, _unitOfWork, _audit, CancellationToken.None);

    [Fact]
    public async Task An_unknown_vehicle_is_a_404()
    {
        var result = await Change(Guid.CreateVersion7(), OperationalStatus.Inactive);

        Assert.True(result.IsFailure);
        Assert.Equal(new ErrorIdentity("fleet", "VEHICLE_NOT_FOUND"), result.FailureDescriptor!.Identity);
        Assert.Equal(ErrorCategory.NotFound, result.FailureDescriptor.Category);
        Assert.Equal("fleet.vehicle_not_found", result.FailureDescriptor.Message.Key);
        Assert.Empty(_audit.Actions);
        Assert.Empty(_audit.Attempts);
    }

    [Fact]
    public async Task Changes_the_status_and_records_VehicleStatusChanged_with_from_and_to()
    {
        var vehicle = Stored(TestVehicles.Registered());

        var result = await Change(vehicle.Id, OperationalStatus.Inactive);

        Assert.True(result.IsSuccess);
        Assert.Equal(OperationalStatus.Inactive, result.Value.OperationalStatus);
        Assert.Equal(VehicleDisplayStatus.Inactive, result.Value.DisplayStatus);
        var recorded = Assert.Single(_audit.Actions);
        Assert.Equal("Fleet", recorded.Module);
        Assert.Equal("VehicleStatusChanged", recorded.Action);
        Assert.Equal("Vehicle", recorded.EntityType);
        Assert.Equal(vehicle.Id.ToString(), recorded.EntityId);
        Assert.Equal("Active", recorded.Metadata!["from"]);
        Assert.Equal("Inactive", recorded.Metadata["to"]);
        Assert.Empty(_audit.Attempts);
    }

    [Fact]
    public async Task The_current_status_is_a_200_without_event_or_audit()
    {
        // F-8.
        var vehicle = Stored(TestVehicles.Registered());

        var result = await Change(vehicle.Id, OperationalStatus.Active);

        Assert.True(result.IsSuccess);
        Assert.Equal(OperationalStatus.Active, result.Value.OperationalStatus);
        Assert.Empty(vehicle.DomainEvents);
        Assert.Empty(_audit.Actions);
        Assert.Empty(_audit.Attempts);
    }

    [Fact]
    public async Task A_committed_vehicle_set_Inactive_is_refused_and_the_attempt_is_recorded()
    {
        // F-3; the refusal is audited as a rejected attempt (docs/plans/fleet.md, "Business audit").
        var vehicle = Stored(TestVehicles.Registered().CommittedTo(Guid.CreateVersion7()));

        var refused = await Assert.ThrowsAsync<BusinessRuleValidationException>(() => Change(vehicle.Id, OperationalStatus.Inactive));

        Assert.Equal("VEHICLE_HAS_MISSION_COMMITMENT", refused.Rule.Code);
        Assert.Equal(OperationalStatus.Active, vehicle.OperationalStatus);
        Assert.Empty(_audit.Actions);
        var attempt = Assert.Single(_audit.Attempts);
        Assert.Equal("Fleet", attempt.Module);
        Assert.Equal("VehicleStatusChanged", attempt.Action);
        Assert.Equal(AuditOutcome.Rejected, attempt.Outcome);
        Assert.Equal(new AuditFailure("fleet", "VEHICLE_HAS_MISSION_COMMITMENT"), attempt.Failure);
        Assert.Equal("Vehicle", attempt.EntityType);
        Assert.Equal(vehicle.Id.ToString(), attempt.EntityId);
        Assert.Equal("Active", attempt.Metadata!["from"]);
        Assert.Equal("Inactive", attempt.Metadata["to"]);
    }
}

public sealed class GetVehicleHandlerTests
{
    private sealed class FakeReadModel(VehicleView? view) : IVehicleReadModel
    {
        public Guid? AskedFor { get; private set; }

        public Task<VehicleView?> GetAsync(Guid vehicleId, CancellationToken cancellationToken)
        {
            AskedFor = vehicleId;
            return Task.FromResult(view);
        }

        // Added when the port gained the available-vehicles read (round 3). This fake serves GetVehicle
        // only; the available list has its own fake and its own tests. Throwing keeps a mistaken call
        // visible instead of answering an empty list.
        public Task<IReadOnlyList<AvailableVehicleView>> GetAvailableAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("This fake serves GetVehicle only.");

        // Added when the port gained the Contracts snapshot (round 5), for the same reason.
        public Task<VehicleSnapshot?> GetSnapshotAsync(Guid vehicleId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("This fake serves GetVehicle only.");
    }

    [Fact]
    public async Task Returns_the_view()
    {
        var view = VehicleView.From(TestVehicles.Registered());
        var readModel = new FakeReadModel(view);

        var result = await GetVehicleHandler.Handle(new GetVehicle(view.Id), readModel, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Same(view, result.Value);
        Assert.Equal(view.Id, readModel.AskedFor);
    }

    [Fact]
    public async Task An_unknown_vehicle_is_a_404()
    {
        var result = await GetVehicleHandler.Handle(new GetVehicle(Guid.CreateVersion7()), new FakeReadModel(null), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(new ErrorIdentity("fleet", "VEHICLE_NOT_FOUND"), result.FailureDescriptor!.Identity);
        Assert.Equal(ErrorCategory.NotFound, result.FailureDescriptor.Category);
    }

    [Fact]
    public void The_query_handler_declares_no_unit_of_work()
    {
        var parameters = typeof(GetVehicleHandler).GetMethod(nameof(GetVehicleHandler.Handle))!.GetParameters();

        Assert.DoesNotContain(parameters, parameter => parameter.ParameterType == typeof(MPCore.Persistence.Abstractions.IUnitOfWork));
        Assert.DoesNotContain(parameters, parameter => parameter.ParameterType == typeof(IVehicleRepository));
    }
}
