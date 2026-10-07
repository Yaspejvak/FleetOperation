using Fserp.FleetOperations.Modules.Drivers.Domain;
using Fserp.FleetOperations.Modules.Drivers.Domain.Events;
using Fserp.FleetOperations.Modules.Fleet.Contracts;
using MPCore.Domain.Rules;

namespace Fserp.FleetOperations.UnitTests.Drivers;

/// <summary>
/// <c>DriverName</c> (docs/plans/drivers.md, D-1): the full name is the only profile field, non-empty
/// after trimming, kept as written otherwise.
/// </summary>
public sealed class DriverNameTests
{
    [Theory]
    [InlineData("Ada Lovelace", "Ada Lovelace")]
    [InlineData("  Ada Lovelace  ", "Ada Lovelace")]
    [InlineData("\tGrace Hopper\n", "Grace Hopper")]
    public void A_name_is_trimmed_and_otherwise_kept_as_written(string entered, string expected)
    {
        Assert.Equal(expected, DriverName.Create(entered).Value);
    }

    [Fact]
    public void A_name_is_not_upper_cased_because_a_person_is_not_an_identifier()
    {
        // Unlike PlateNumber, which normalizes case because it identifies a vehicle.
        Assert.Equal("ada LOVELACE", DriverName.Create("ada LOVELACE").Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    [InlineData(null)]
    public void An_empty_name_breaks_DRIVER_NAME_REQUIRED(string? entered)
    {
        var refused = Assert.Throws<BusinessRuleValidationException>(() => DriverName.Create(entered));

        Assert.Equal("drivers", refused.Rule.ErrorDomain);
        Assert.Equal("DRIVER_NAME_REQUIRED", refused.Rule.Code);
        Assert.Equal("drivers.driver_name_required", refused.Rule.MessageKey);
    }

    [Fact]
    public void The_value_object_carries_no_length_rule()
    {
        // L-17: the 128-character cap is input shape and a column bound, not an invariant. The plan lists
        // no length rule, so the domain accepts a longer name and the validator is what refuses it.
        var long_name = new string('a', DriverName.MaxLength + 1);

        Assert.Equal(long_name, DriverName.Create(long_name).Value);
    }

    [Fact]
    public void Two_names_with_the_same_normalized_value_are_equal()
    {
        Assert.Equal(DriverName.Create(" Ada "), DriverName.Create("Ada"));
    }
}

/// <summary>
/// <c>Driver.Register</c> (docs/plans/drivers.md, "Invariants"; D-4, D-5; L-18, L-19).
/// </summary>
public sealed class DriverRegistrationTests
{
    private static Driver Register(params VehicleType[] types) =>
        Driver.Register(Guid.CreateVersion7(TestDrivers.Now), DriverName.Create("Ada"), types, TestDrivers.Now);

    [Fact]
    public void A_registered_driver_is_Active_uncommitted_and_qualified_for_what_was_given()
    {
        var driver = Register(VehicleType.Van, VehicleType.HeavyTruck);

        Assert.Equal(OperationalStatus.Active, driver.OperationalStatus);
        Assert.Null(driver.CommittedMissionId);
        Assert.Equal(
            [VehicleType.Van, VehicleType.HeavyTruck],
            driver.Qualifications.Select(qualification => qualification.VehicleType));
    }

    [Fact]
    public void Registration_raises_DriverRegistered_and_nothing_else()
    {
        var driver = Register(VehicleType.Van);

        Assert.Equal(new DriverRegistered(driver.Id), Assert.Single(driver.DomainEvents));
    }

    [Fact]
    public void Every_qualification_gets_a_version_7_identity_timestamped_by_the_clock_it_was_given()
    {
        var driver = Register(VehicleType.Van, VehicleType.Truck);

        Assert.All(driver.Qualifications, qualification =>
        {
            Assert.Equal(7, qualification.Id.Version);
            Assert.Equal(TestDrivers.Now.ToUnixTimeMilliseconds(), TimestampOf(qualification.Id));
        });
        // Distinct identities, so two qualifications are two rows.
        Assert.Equal(2, driver.Qualifications.Select(qualification => qualification.Id).Distinct().Count());
    }

    [Fact]
    public void A_qualification_carries_the_vehicle_type_and_nothing_else()
    {
        // D-4: no licence class, no expiry. By reflection, so a fifth property cannot appear unnoticed.
        var declared = typeof(Qualification)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(property => property.Name)
            .Order();

        Assert.Equal(["CreatedOnUtc", "Id", "ModifiedOnUtc", "VehicleType"], declared);
    }

    [Fact]
    public void No_qualification_breaks_DRIVER_QUALIFICATION_REQUIRED()
    {
        // D-5.
        var refused = Assert.Throws<BusinessRuleValidationException>(() => Register());

        Assert.Equal("drivers", refused.Rule.ErrorDomain);
        Assert.Equal("DRIVER_QUALIFICATION_REQUIRED", refused.Rule.Code);
    }

    [Fact]
    public void The_same_vehicle_type_twice_breaks_DRIVER_QUALIFICATION_DUPLICATE()
    {
        var refused = Assert.Throws<BusinessRuleValidationException>(
            () => Register(VehicleType.Van, VehicleType.Truck, VehicleType.Van));

        Assert.Equal("DRIVER_QUALIFICATION_DUPLICATE", refused.Rule.Code);
    }

    [Fact]
    public void An_empty_list_is_the_required_rule_and_not_the_duplicate_rule()
    {
        // L-19: required is checked first. The order is unobservable — an empty list cannot contain a
        // duplicate — and is fixed so the code and its tests cannot drift apart.
        var refused = Assert.Throws<BusinessRuleValidationException>(() => Register());

        Assert.NotEqual("DRIVER_QUALIFICATION_DUPLICATE", refused.Rule.Code);
    }

    [Fact]
    public void A_vehicle_type_outside_the_closed_list_is_a_defect_not_a_business_rule()
    {
        // The validator refuses it first; reaching the aggregate with one means the validator was bypassed.
        Assert.Throws<ArgumentOutOfRangeException>(() => Register((VehicleType)99));
    }

    [Fact]
    public void An_empty_identity_is_refused()
    {
        Assert.Throws<ArgumentException>(() =>
            Driver.Register(Guid.Empty, DriverName.Create("Ada"), [VehicleType.Van], TestDrivers.Now));
    }

    private static long TimestampOf(Guid id)
    {
        var bytes = id.ToByteArray(bigEndian: true);
        return ((long)bytes[0] << 40) | ((long)bytes[1] << 32) | ((long)bytes[2] << 24)
            | ((long)bytes[3] << 16) | ((long)bytes[4] << 8) | bytes[5];
    }
}

/// <summary>
/// <c>Driver.ChangeStatus</c> (D-2): Active and Inactive only, the current status is a no-op, and a
/// committed driver cannot be deactivated.
/// </summary>
public sealed class DriverStatusTests
{
    [Fact]
    public void A_real_change_raises_one_event_with_from_and_to()
    {
        var driver = TestDrivers.Registered();

        Assert.True(driver.ChangeStatus(OperationalStatus.Inactive));

        Assert.Equal(OperationalStatus.Inactive, driver.OperationalStatus);
        Assert.Equal(
            new DriverStatusChanged(driver.Id, OperationalStatus.Active, OperationalStatus.Inactive),
            Assert.Single(driver.DomainEvents));
    }

    [Fact]
    public void The_current_status_is_a_no_op_with_no_event()
    {
        // docs/plans/drivers.md: "ChangeStatus to the current status is a no-op, as in Fleet" (F-8).
        var driver = TestDrivers.Registered();

        Assert.False(driver.ChangeStatus(OperationalStatus.Active));

        Assert.Equal(OperationalStatus.Active, driver.OperationalStatus);
        Assert.Empty(driver.DomainEvents);
    }

    [Fact]
    public void Reactivation_is_a_real_change_from_Inactive()
    {
        var driver = TestDrivers.Registered();
        driver.ChangeStatus(OperationalStatus.Inactive);
        driver.ClearEvents();

        Assert.True(driver.ChangeStatus(OperationalStatus.Active));

        Assert.Equal(
            new DriverStatusChanged(driver.Id, OperationalStatus.Inactive, OperationalStatus.Active),
            Assert.Single(driver.DomainEvents));
    }

    [Fact]
    public void A_committed_driver_set_Inactive_breaks_DRIVER_HAS_MISSION_COMMITMENT()
    {
        // D-2, the same rule as F-3 for a vehicle.
        var mission = Guid.CreateVersion7();
        var driver = TestDrivers.Registered().CommittedTo(mission);

        var refused = Assert.Throws<BusinessRuleValidationException>(() => driver.ChangeStatus(OperationalStatus.Inactive));

        Assert.Equal("drivers", refused.Rule.ErrorDomain);
        Assert.Equal("DRIVER_HAS_MISSION_COMMITMENT", refused.Rule.Code);
        // Refused before anything changed: the status and the commitment are both as they were.
        Assert.Equal(OperationalStatus.Active, driver.OperationalStatus);
        Assert.Equal(mission, driver.CommittedMissionId);
        Assert.Empty(driver.DomainEvents);
    }

    [Fact]
    public void A_committed_driver_set_Active_is_still_the_no_op_and_not_a_refusal()
    {
        // The no-op path is taken before the rule is read, exactly as Fleet's is.
        var driver = TestDrivers.Registered().CommittedTo(Guid.CreateVersion7());

        Assert.False(driver.ChangeStatus(OperationalStatus.Active));

        Assert.Empty(driver.DomainEvents);
    }

    [Fact]
    public void A_status_outside_the_closed_list_is_a_defect_not_a_business_rule()
    {
        var driver = TestDrivers.Registered();

        Assert.Throws<ArgumentOutOfRangeException>(() => driver.ChangeStatus((OperationalStatus)9));
    }
}

/// <summary>
/// <c>Driver.CommitToMission</c> and <c>Driver.ReleaseFromMission</c>: assignment preconditions 6, 7 and 8
/// of docs/architecture.md, decision 1, and the release rule.
/// </summary>
public sealed class DriverCommitmentTests
{
    private static readonly Guid Mission = Guid.CreateVersion7();

    [Fact]
    public void Committing_an_available_qualified_driver_sets_the_mission_and_raises_the_event()
    {
        var driver = TestDrivers.Registered("Ada", VehicleType.Van);

        Assert.True(driver.CommitToMission(Mission, VehicleType.Van));

        Assert.Equal(Mission, driver.CommittedMissionId);
        Assert.Equal(new DriverCommittedToMission(driver.Id, Mission), Assert.Single(driver.DomainEvents));
    }

    [Fact]
    public void An_Inactive_driver_breaks_DRIVER_NOT_ACTIVE()
    {
        // Precondition 6.
        var driver = TestDrivers.Registered("Ada", VehicleType.Van);
        driver.ChangeStatus(OperationalStatus.Inactive);
        driver.ClearEvents();

        var refused = Assert.Throws<BusinessRuleValidationException>(() => driver.CommitToMission(Mission, VehicleType.Van));

        Assert.Equal("DRIVER_NOT_ACTIVE", refused.Rule.Code);
        Assert.Null(driver.CommittedMissionId);
        Assert.Empty(driver.DomainEvents);
    }

    [Fact]
    public void A_driver_committed_to_another_mission_breaks_DRIVER_NOT_AVAILABLE()
    {
        // Precondition 7.
        var driver = TestDrivers.Registered("Ada", VehicleType.Van).CommittedTo(Mission, VehicleType.Van);
        var otherMission = Guid.CreateVersion7();

        var refused = Assert.Throws<BusinessRuleValidationException>(() => driver.CommitToMission(otherMission, VehicleType.Van));

        Assert.Equal("DRIVER_NOT_AVAILABLE", refused.Rule.Code);
        Assert.Equal(Mission, driver.CommittedMissionId);
        Assert.Empty(driver.DomainEvents);
    }

    [Fact]
    public void A_driver_without_a_qualification_for_the_type_breaks_DRIVER_NOT_QUALIFIED()
    {
        // Precondition 8.
        var driver = TestDrivers.Registered("Ada", VehicleType.Van);

        var refused = Assert.Throws<BusinessRuleValidationException>(() => driver.CommitToMission(Mission, VehicleType.HeavyTruck));

        Assert.Equal("drivers", refused.Rule.ErrorDomain);
        Assert.Equal("DRIVER_NOT_QUALIFIED", refused.Rule.Code);
        Assert.Null(driver.CommittedMissionId);
    }

    [Fact]
    public void One_qualification_among_several_is_enough()
    {
        var driver = TestDrivers.Registered("Ada", VehicleType.Van, VehicleType.HeavyTruck);

        Assert.True(driver.CommitToMission(Mission, VehicleType.HeavyTruck));
    }

    [Fact]
    public void Committing_to_the_mission_the_driver_already_holds_is_a_no_op_with_no_event()
    {
        // The same treatment Vehicle.CommitToMission gives the mission it already holds. Both NOT_AVAILABLE
        // rules are worded "committed to a different mission", so this call is not a refusal; answering it
        // with a second event would record one fact twice.
        var driver = TestDrivers.Registered("Ada", VehicleType.Van).CommittedTo(Mission, VehicleType.Van);

        Assert.False(driver.CommitToMission(Mission, VehicleType.Van));

        Assert.Equal(Mission, driver.CommittedMissionId);
        Assert.Empty(driver.DomainEvents);
    }

    [Fact]
    public void An_Inactive_driver_rejoining_the_mission_it_already_holds_is_still_the_no_op()
    {
        // The no-op is taken before any rule is read. This state cannot be reached in production — a
        // committed driver cannot be deactivated (D-2) — so the test only pins where the branch sits.
        var driver = TestDrivers.Registered("Ada", VehicleType.Van).CommittedTo(Mission, VehicleType.Van);

        Assert.False(driver.CommitToMission(Mission, VehicleType.HeavyTruck));
    }

    [Fact]
    public void An_empty_mission_identity_is_refused()
    {
        var driver = TestDrivers.Registered("Ada", VehicleType.Van);

        Assert.Throws<ArgumentException>(() => driver.CommitToMission(Guid.Empty, VehicleType.Van));
    }

    [Fact]
    public void Releasing_the_mission_that_holds_the_driver_clears_it_and_raises_the_event()
    {
        var driver = TestDrivers.Registered("Ada", VehicleType.Van).CommittedTo(Mission, VehicleType.Van);

        driver.ReleaseFromMission(Mission);

        Assert.Null(driver.CommittedMissionId);
        Assert.Equal(new DriverReleasedFromMission(driver.Id, Mission), Assert.Single(driver.DomainEvents));
    }

    [Fact]
    public void Releasing_an_uncommitted_driver_breaks_DRIVER_NOT_COMMITTED_TO_MISSION()
    {
        var driver = TestDrivers.Registered("Ada", VehicleType.Van);

        var refused = Assert.Throws<BusinessRuleValidationException>(() => driver.ReleaseFromMission(Mission));

        Assert.Equal("DRIVER_NOT_COMMITTED_TO_MISSION", refused.Rule.Code);
        Assert.Empty(driver.DomainEvents);
    }

    [Fact]
    public void One_mission_cannot_release_a_driver_another_mission_holds()
    {
        var driver = TestDrivers.Registered("Ada", VehicleType.Van).CommittedTo(Mission, VehicleType.Van);

        var refused = Assert.Throws<BusinessRuleValidationException>(() => driver.ReleaseFromMission(Guid.CreateVersion7()));

        Assert.Equal("DRIVER_NOT_COMMITTED_TO_MISSION", refused.Rule.Code);
        Assert.Equal(Mission, driver.CommittedMissionId);
    }

    [Fact]
    public void A_released_driver_can_be_deactivated_again()
    {
        // The commitment is what refuses deactivation (D-2); releasing it removes the refusal.
        var driver = TestDrivers.Registered("Ada", VehicleType.Van).CommittedTo(Mission, VehicleType.Van);
        driver.ReleaseFromMission(Mission);

        Assert.True(driver.ChangeStatus(OperationalStatus.Inactive));
    }
}

/// <summary>
/// The one definition of "available" for a driver (docs/plans/drivers.md): the read model translates this
/// expression to SQL, so evaluating it in memory tests the same predicate the database runs.
/// </summary>
public sealed class DriverAvailabilityTests
{
    [Fact]
    public void A_newly_registered_driver_is_available()
    {
        Assert.True(DriverAvailability.IsAvailable(TestDrivers.Registered()));
    }

    [Fact]
    public void An_Inactive_driver_is_not_available()
    {
        var driver = TestDrivers.Registered();
        driver.ChangeStatus(OperationalStatus.Inactive);

        Assert.False(DriverAvailability.IsAvailable(driver));
    }

    [Fact]
    public void A_committed_driver_is_not_available()
    {
        Assert.False(DriverAvailability.IsAvailable(TestDrivers.Registered().CommittedTo(Guid.CreateVersion7())));
    }

    [Fact]
    public void A_released_driver_is_available_again()
    {
        var mission = Guid.CreateVersion7();
        var driver = TestDrivers.Registered().CommittedTo(mission);
        driver.ReleaseFromMission(mission);

        Assert.True(DriverAvailability.IsAvailable(driver));
    }

    [Fact]
    public void The_specification_names_the_two_conditions_the_plan_names()
    {
        // The read model translates this expression; its text pins what the SQL will ask for. There is no
        // maintenance condition for a driver: the plan names two, not three.
        var text = DriverAvailability.Specification.ToString();

        Assert.Contains(nameof(Driver.OperationalStatus), text, StringComparison.Ordinal);
        Assert.Contains(nameof(Driver.CommittedMissionId), text, StringComparison.Ordinal);
    }
}
