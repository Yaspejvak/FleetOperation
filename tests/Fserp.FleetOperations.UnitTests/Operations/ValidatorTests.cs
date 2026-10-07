using FluentValidation.Results;
using Fserp.FleetOperations.Modules.Operations.Application.Commands;
using Fserp.FleetOperations.Modules.Operations.Application.Queries;
using Fserp.FleetOperations.Modules.Operations.Application.Validators;

namespace Fserp.FleetOperations.UnitTests.Operations;

/// <summary>
/// The input-shape validators of the Operations module. They run before the handler and reach the caller
/// as <c>400</c> with one violation per field; the value objects and the aggregate remain the enforcement.
/// </summary>
public sealed class OperationsValidatorTests
{
    private static ValidationFailure Single(ValidationResult result, string property) =>
        Assert.Single(result.Errors, error => error.PropertyName == property);

    [Fact]
    public void Create_refuses_an_empty_origin_with_the_locations_own_code_and_key()
    {
        var result = new CreateMissionValidator().Validate(new CreateMission(string.Empty, "Isfahan", 800m));

        var failure = Single(result, nameof(CreateMission.Origin));
        Assert.Equal("MISSION_LOCATION_REQUIRED", failure.ErrorCode);
        Assert.Equal("operations.mission_location_required", failure.ErrorMessage);
    }

    [Fact]
    public void Create_refuses_an_empty_destination()
    {
        var result = new CreateMissionValidator().Validate(new CreateMission("Tehran", "   ", 800m));

        Assert.Equal("MISSION_LOCATION_REQUIRED", Single(result, nameof(CreateMission.Destination)).ErrorCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_refuses_a_non_positive_capacity_with_the_value_objects_code(decimal kg)
    {
        var result = new CreateMissionValidator().Validate(new CreateMission("Tehran", "Isfahan", kg));

        var failure = Single(result, nameof(CreateMission.RequiredCapacityKg));
        Assert.Equal("MISSION_REQUIRED_CAPACITY_MUST_BE_POSITIVE", failure.ErrorCode);
        Assert.Equal("operations.mission_required_capacity_must_be_positive", failure.ErrorMessage);
    }

    [Fact]
    public void Create_accepts_an_origin_equal_to_the_destination()
    {
        // O-7: no validator compares the two, deliberately.
        Assert.True(new CreateMissionValidator().Validate(new CreateMission("Tehran", "Tehran", 1m)).IsValid);
    }

    [Fact]
    public void Schedule_requires_a_mission_id_and_a_scheduled_time()
    {
        var result = new ScheduleMissionValidator().Validate(new ScheduleMission(Guid.Empty, default));

        Assert.Equal(2, result.Errors.Count);
        Assert.Contains(result.Errors, error => error.PropertyName == nameof(ScheduleMission.MissionId));
        Assert.Contains(result.Errors, error => error.PropertyName == nameof(ScheduleMission.ScheduledAt));
    }

    [Fact]
    public void Schedule_accepts_a_time_in_the_past()
    {
        // O-2: there is no "must be in the future" rule, and this test makes that absence deliberate.
        var past = new DateTimeOffset(1999, 12, 31, 23, 59, 0, TimeSpan.Zero);

        Assert.True(new ScheduleMissionValidator().Validate(new ScheduleMission(Guid.CreateVersion7(), past)).IsValid);
    }

    [Fact]
    public void Assign_requires_all_three_identities()
    {
        var result = new AssignMissionValidator().Validate(new AssignMission(Guid.Empty, Guid.Empty, Guid.Empty));

        Assert.Equal(3, result.Errors.Count);
    }

    [Fact]
    public void Assign_checks_no_rule_that_needs_state()
    {
        // Preconditions 1 to 8 all need state, so none of them is here: a validator never reads the
        // database (docs/architecture.md).
        Assert.True(new AssignMissionValidator()
            .Validate(new AssignMission(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7()))
            .IsValid);
    }

    [Fact]
    public void The_three_data_less_commands_require_only_the_mission_id()
    {
        Assert.False(new StartMissionValidator().Validate(new StartMission(Guid.Empty)).IsValid);
        Assert.False(new CompleteMissionValidator().Validate(new CompleteMission(Guid.Empty)).IsValid);
        Assert.False(new CancelMissionValidator().Validate(new CancelMission(Guid.Empty)).IsValid);

        var id = Guid.CreateVersion7();
        Assert.True(new StartMissionValidator().Validate(new StartMission(id)).IsValid);
        Assert.True(new CompleteMissionValidator().Validate(new CompleteMission(id)).IsValid);
        Assert.True(new CancelMissionValidator().Validate(new CancelMission(id)).IsValid);
    }

    [Fact]
    public void Get_refuses_an_empty_identity_so_an_unparsable_grpc_id_is_a_validation_failure()
    {
        // L-7: Guid.TryParse of "not-a-uuid" yields Guid.Empty, which this refuses, rather than the gRPC
        // adapter fabricating a code of its own.
        Assert.False(new GetMissionValidator().Validate(new GetMission(Guid.Empty)).IsValid);
        Assert.True(new GetMissionValidator().Validate(new GetMission(Guid.CreateVersion7())).IsValid);
    }
}
