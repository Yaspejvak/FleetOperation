using Fserp.FleetOperations.Modules.Drivers.Application.Commands;
using Fserp.FleetOperations.Modules.Drivers.Application.Queries;
using Fserp.FleetOperations.Modules.Drivers.Application.Validators;
using Fserp.FleetOperations.Modules.Drivers.Domain;
using Fserp.FleetOperations.Modules.Fleet.Contracts;
using MPCore.Validation.FluentValidation;

namespace Fserp.FleetOperations.UnitTests.Drivers;

public sealed class RegisterDriverValidatorTests
{
    private readonly RegisterDriverValidator _validator = new();

    private static RegisterDriver Command(string fullName, params VehicleType[] types) => new(fullName, types);

    [Fact]
    public void A_well_formed_registration_passes()
    {
        Assert.True(_validator.Validate(Command("Ada Lovelace", VehicleType.Van)).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_name_is_a_field_violation_with_the_name_rule(string fullName)
    {
        var result = _validator.Validate(Command(fullName, VehicleType.Van));

        var violation = ValidationFailures.ToViolation(Assert.Single(result.Errors));
        Assert.Equal("full_name", violation.FieldPath);
        Assert.Equal("DRIVER_NAME_REQUIRED", violation.RuleCode);
        Assert.Equal("drivers.driver_name_required", violation.Message.Key);
    }

    [Fact]
    public void A_name_longer_than_128_characters_after_trimming_is_a_field_violation_on_full_name()
    {
        // L-17: a 400 with field full_name, and deliberately no DRIVER_NAME_TOO_LONG business rule — the
        // plan lists no such invariant.
        var result = _validator.Validate(Command(new string('a', DriverName.MaxLength + 1), VehicleType.Van));

        var violation = ValidationFailures.ToViolation(Assert.Single(result.Errors));
        Assert.Equal("full_name", violation.FieldPath);
        Assert.DoesNotContain("DRIVER_NAME_TOO_LONG", violation.RuleCode, StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_of_128_characters_after_trimming_passes()
    {
        // The validator measures what DriverName keeps and what the column stores, so the three agree.
        var padded = "   " + new string('a', DriverName.MaxLength) + "   ";

        Assert.True(_validator.Validate(Command(padded, VehicleType.Van)).IsValid);
        Assert.Equal(DriverName.MaxLength, DriverName.Create(padded).Value.Length);
    }

    [Fact]
    public void No_vehicle_type_is_a_field_violation_with_the_qualification_rule()
    {
        // D-5, mirrored from the domain so the caller sees one vocabulary.
        var result = _validator.Validate(Command("Ada"));

        var violation = ValidationFailures.ToViolation(Assert.Single(result.Errors));
        Assert.Equal("vehicle_types", violation.FieldPath);
        Assert.Equal("DRIVER_QUALIFICATION_REQUIRED", violation.RuleCode);
        Assert.Equal("drivers.driver_qualification_required", violation.Message.Key);
    }

    [Fact]
    public void A_repeated_vehicle_type_is_a_field_violation_with_the_duplicate_rule()
    {
        var result = _validator.Validate(Command("Ada", VehicleType.Van, VehicleType.Van));

        var violation = ValidationFailures.ToViolation(Assert.Single(result.Errors));
        Assert.Equal("vehicle_types", violation.FieldPath);
        Assert.Equal("DRIVER_QUALIFICATION_DUPLICATE", violation.RuleCode);
        Assert.Equal("drivers.driver_qualification_duplicate", violation.Message.Key);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void A_type_outside_the_closed_list_is_a_field_violation(int type)
    {
        var result = _validator.Validate(Command("Ada", (VehicleType)type));

        var violation = ValidationFailures.ToViolation(Assert.Single(result.Errors));
        Assert.StartsWith("vehicle_types", violation.FieldPath, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_problem_is_reported_at_once()
    {
        // An empty name and an empty list: two violations, not one.
        var result = _validator.Validate(Command(string.Empty));

        Assert.Equal(2, result.Errors.Count);
    }
}

public sealed class ChangeDriverStatusValidatorTests
{
    private readonly ChangeDriverStatusValidator _validator = new();

    [Theory]
    [InlineData(OperationalStatus.Active)]
    [InlineData(OperationalStatus.Inactive)]
    public void Active_or_Inactive_passes(OperationalStatus status)
    {
        Assert.True(_validator.Validate(new ChangeDriverStatus(Guid.CreateVersion7(), status)).IsValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Any_other_status_is_a_field_violation(int status)
    {
        var result = _validator.Validate(new ChangeDriverStatus(Guid.CreateVersion7(), (OperationalStatus)status));

        Assert.Equal("status", ValidationFailures.ToViolation(Assert.Single(result.Errors)).FieldPath);
    }

    [Fact]
    public void An_empty_driver_id_is_a_field_violation()
    {
        var result = _validator.Validate(new ChangeDriverStatus(Guid.Empty, OperationalStatus.Active));

        Assert.Equal("driver_id", ValidationFailures.ToViolation(Assert.Single(result.Errors)).FieldPath);
    }
}

public sealed class GetDriverValidatorTests
{
    [Fact]
    public void An_empty_driver_id_is_a_field_violation()
    {
        var result = new GetDriverValidator().Validate(new GetDriver(Guid.Empty));

        Assert.Equal("driver_id", ValidationFailures.ToViolation(Assert.Single(result.Errors)).FieldPath);
    }

    [Fact]
    public void A_driver_id_passes()
    {
        Assert.True(new GetDriverValidator().Validate(new GetDriver(Guid.CreateVersion7())).IsValid);
    }
}
