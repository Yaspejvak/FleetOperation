using Fserp.FleetOperations.Modules.Fleet.Contracts;
using Fserp.FleetOperations.Modules.Fleet.Domain;
using Fserp.FleetOperations.Modules.Fleet.Domain.Events;
using MPCore.Domain.Rules;

namespace Fserp.FleetOperations.UnitTests.Fleet;

public sealed class VehicleRegistrationTests
{
    [Fact]
    public void A_registered_vehicle_is_Active_not_under_maintenance_and_uncommitted()
    {
        var id = Guid.CreateVersion7();

        var vehicle = Vehicle.Register(id, PlateNumber.Create("ab-1"), VehicleType.Van, Capacity.FromKilograms(800m));

        Assert.Equal(id, vehicle.Id);
        Assert.Equal("AB-1", vehicle.PlateNumber.Value);
        Assert.Equal(VehicleType.Van, vehicle.VehicleType);
        Assert.Equal(800m, vehicle.Capacity.Kilograms);
        Assert.Equal(OperationalStatus.Active, vehicle.OperationalStatus);
        Assert.Equal(MaintenanceStatus.NotUnderMaintenance, vehicle.MaintenanceStatus);
        Assert.Null(vehicle.CommittedMissionId);
        Assert.Equal(VehicleDisplayStatus.Active, vehicle.DisplayStatus);
    }

    [Fact]
    public void Registration_raises_VehicleRegistered()
    {
        var id = Guid.CreateVersion7();

        var vehicle = Vehicle.Register(id, PlateNumber.Create("ab-1"), VehicleType.HeavyTruck, Capacity.FromKilograms(1m));

        var raised = Assert.Single(vehicle.DomainEvents);
        Assert.Equal(new VehicleRegistered(id), raised);
    }

    [Fact]
    public void An_empty_identity_is_refused()
    {
        Assert.Throws<ArgumentException>(() =>
            Vehicle.Register(Guid.Empty, PlateNumber.Create("ab-1"), VehicleType.Van, Capacity.FromKilograms(1m)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void A_type_outside_the_closed_list_is_refused(int type)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Vehicle.Register(Guid.CreateVersion7(), PlateNumber.Create("ab-1"), (VehicleType)type, Capacity.FromKilograms(1m)));
    }

    [Fact]
    public void The_status_fields_have_no_public_setter()
    {
        foreach (var name in new[]
        {
            nameof(Vehicle.PlateNumber), nameof(Vehicle.VehicleType), nameof(Vehicle.Capacity),
            nameof(Vehicle.OperationalStatus), nameof(Vehicle.MaintenanceStatus), nameof(Vehicle.CommittedMissionId),
        })
        {
            var setter = typeof(Vehicle).GetProperty(name)!.SetMethod;
            Assert.True(setter is null || !setter.IsPublic, $"{name} must not be publicly settable.");
        }
    }
}

public sealed class VehicleChangeStatusTests
{
    [Fact]
    public void Active_to_Inactive_changes_the_status_and_raises_VehicleStatusChanged()
    {
        var vehicle = TestVehicles.Registered();

        var changed = vehicle.ChangeStatus(OperationalStatus.Inactive);

        Assert.True(changed);
        Assert.Equal(OperationalStatus.Inactive, vehicle.OperationalStatus);
        Assert.Equal(VehicleDisplayStatus.Inactive, vehicle.DisplayStatus);
        var raised = Assert.Single(vehicle.DomainEvents);
        Assert.Equal(new VehicleStatusChanged(vehicle.Id, OperationalStatus.Active, OperationalStatus.Inactive), raised);
    }

    [Fact]
    public void Inactive_to_Active_changes_the_status()
    {
        var vehicle = TestVehicles.Registered();
        vehicle.ChangeStatus(OperationalStatus.Inactive);
        vehicle.ClearEvents();

        Assert.True(vehicle.ChangeStatus(OperationalStatus.Active));
        Assert.Equal(OperationalStatus.Active, vehicle.OperationalStatus);
        Assert.Equal(
            new VehicleStatusChanged(vehicle.Id, OperationalStatus.Inactive, OperationalStatus.Active),
            Assert.Single(vehicle.DomainEvents));
    }

    [Theory]
    [InlineData(OperationalStatus.Active)]
    [InlineData(OperationalStatus.Inactive)]
    public void Setting_the_current_status_is_a_no_op_without_an_event(OperationalStatus status)
    {
        // F-8.
        var vehicle = TestVehicles.Registered();
        if (status == OperationalStatus.Inactive)
        {
            vehicle.ChangeStatus(OperationalStatus.Inactive);
            vehicle.ClearEvents();
        }

        Assert.False(vehicle.ChangeStatus(status));
        Assert.Equal(status, vehicle.OperationalStatus);
        Assert.Empty(vehicle.DomainEvents);
    }

    [Fact]
    public void A_committed_vehicle_cannot_be_set_Inactive()
    {
        // F-3.
        var vehicle = TestVehicles.Registered().CommittedTo(Guid.CreateVersion7());

        var refused = Assert.Throws<BusinessRuleValidationException>(() => vehicle.ChangeStatus(OperationalStatus.Inactive));

        Assert.Equal("fleet", refused.Rule.ErrorDomain);
        Assert.Equal("VEHICLE_HAS_MISSION_COMMITMENT", refused.Rule.Code);
        Assert.Equal("fleet.vehicle_has_mission_commitment", refused.Rule.MessageKey);
        Assert.Equal(OperationalStatus.Active, vehicle.OperationalStatus);
        Assert.Empty(vehicle.DomainEvents);
    }

    [Fact]
    public void A_committed_vehicle_set_to_its_current_Active_status_is_still_a_no_op()
    {
        var mission = Guid.CreateVersion7();
        var vehicle = TestVehicles.Registered().CommittedTo(mission);

        Assert.False(vehicle.ChangeStatus(OperationalStatus.Active));
        Assert.Equal(mission, vehicle.CommittedMissionId);
    }

    [Fact]
    public void Changing_the_status_leaves_maintenance_and_commitment_untouched()
    {
        // F-1: the two status fields are independent.
        var vehicle = TestVehicles.Registered();

        vehicle.ChangeStatus(OperationalStatus.Inactive);

        Assert.Equal(MaintenanceStatus.NotUnderMaintenance, vehicle.MaintenanceStatus);
        Assert.Null(vehicle.CommittedMissionId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void An_undefined_status_is_refused(int status)
    {
        var vehicle = TestVehicles.Registered();

        Assert.Throws<ArgumentOutOfRangeException>(() => vehicle.ChangeStatus((OperationalStatus)status));
        Assert.Equal(OperationalStatus.Active, vehicle.OperationalStatus);
    }
}
