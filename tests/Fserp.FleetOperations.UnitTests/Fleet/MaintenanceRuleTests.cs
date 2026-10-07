using Fserp.FleetOperations.Modules.Fleet.Domain;
using Fserp.FleetOperations.Modules.Fleet.Domain.Rules;

namespace Fserp.FleetOperations.UnitTests.Fleet;

/// <summary>
/// The two rule classes added in round 2, read on their own rather than through the aggregate: the state
/// that breaks each, and the identity and message key each reports it under. That every declared code has
/// a text is enforced for the whole module by <c>FleetMessageCoverageTests</c>.
/// </summary>
public sealed class MaintenanceRuleTests
{
    [Theory]
    [InlineData(MaintenanceStatus.UnderMaintenance, true)]
    [InlineData(MaintenanceStatus.NotUnderMaintenance, false)]
    public void The_already_rule_is_broken_only_while_the_vehicle_is_under_maintenance(MaintenanceStatus status, bool broken)
    {
        var rule = new VehicleAlreadyUnderMaintenanceRule(status);

        Assert.Equal(broken, rule.IsBroken());
        Assert.Equal(FleetErrors.Domain, rule.ErrorDomain);
        Assert.Equal("VEHICLE_ALREADY_UNDER_MAINTENANCE", rule.Code);
        Assert.Equal("fleet.vehicle_already_under_maintenance", rule.MessageKey);
    }

    [Theory]
    [InlineData(MaintenanceStatus.NotUnderMaintenance, true)]
    [InlineData(MaintenanceStatus.UnderMaintenance, false)]
    public void The_not_rule_is_broken_whenever_the_vehicle_is_not_under_maintenance(MaintenanceStatus status, bool broken)
    {
        var rule = new VehicleNotUnderMaintenanceRule(status);

        Assert.Equal(broken, rule.IsBroken());
        Assert.Equal(FleetErrors.Domain, rule.ErrorDomain);
        Assert.Equal("VEHICLE_NOT_UNDER_MAINTENANCE", rule.Code);
        Assert.Equal("fleet.vehicle_not_under_maintenance", rule.MessageKey);
    }

    [Fact]
    public void A_value_outside_the_enum_is_never_read_as_under_maintenance()
    {
        // MaintenanceStatus has no zero member. A value that is not UnderMaintenance must not let a second
        // Start through, and must not let Complete through either.
        Assert.False(new VehicleAlreadyUnderMaintenanceRule(0).IsBroken());
        Assert.True(new VehicleNotUnderMaintenanceRule(0).IsBroken());
    }

    [Fact]
    public void The_two_codes_are_the_ones_FleetErrors_declares()
    {
        Assert.Equal("VEHICLE_ALREADY_UNDER_MAINTENANCE", FleetErrors.AlreadyUnderMaintenance);
        Assert.Equal("VEHICLE_NOT_UNDER_MAINTENANCE", FleetErrors.NotUnderMaintenance);
    }
}
