using Fserp.FleetOperations.Modules.Fleet.Application.Commands;
using Fserp.FleetOperations.Modules.Fleet.Application.Queries;
using Fserp.FleetOperations.Modules.Fleet.Application.Validators;
using Fserp.FleetOperations.Modules.Fleet.Contracts;
using Fserp.FleetOperations.Modules.Fleet.Domain;
using MPCore.Validation.FluentValidation;

namespace Fserp.FleetOperations.UnitTests.Fleet;

public sealed class RegisterVehicleValidatorTests
{
    private readonly RegisterVehicleValidator _validator = new();

    [Fact]
    public void A_well_formed_registration_passes()
    {
        Assert.True(_validator.Validate(new RegisterVehicle("AB-1", VehicleType.Van, 0.5m)).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_plate_is_a_field_violation_with_the_plate_rule(string plate)
    {
        var result = _validator.Validate(new RegisterVehicle(plate, VehicleType.Van, 1m));

        var violation = ValidationFailures.ToViolation(Assert.Single(result.Errors));
        Assert.Equal("plate_number", violation.FieldPath);
        Assert.Equal("VEHICLE_PLATE_NUMBER_REQUIRED", violation.RuleCode);
        Assert.Equal("fleet.vehicle_plate_number_required", violation.Message.Key);
    }

    [Theory]
    [InlineData("ABCDEFGHIJ-123456")]
    [InlineData("  abcdefghij-123456  ")]
    public void A_plate_longer_than_16_characters_is_a_field_violation_with_the_length_rule(string plate)
    {
        var result = _validator.Validate(new RegisterVehicle(plate, VehicleType.Van, 1m));

        var violation = ValidationFailures.ToViolation(Assert.Single(result.Errors));
        Assert.Equal("plate_number", violation.FieldPath);
        Assert.Equal("VEHICLE_PLATE_NUMBER_TOO_LONG", violation.RuleCode);
        Assert.Equal("fleet.vehicle_plate_number_too_long", violation.Message.Key);
    }

    [Theory]
    [InlineData("ABCDEFGHIJ-12345")]
    [InlineData("    abcdefghij-12345    ")]
    public void A_plate_of_16_characters_after_normalization_passes(string plate)
    {
        // The validator measures what PlateNumber keeps, so it agrees with the value object.
        Assert.True(_validator.Validate(new RegisterVehicle(plate, VehicleType.Van, 1m)).IsValid);
        Assert.Equal(16, PlateNumber.Create(plate).Value.Length);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void A_capacity_that_is_not_positive_is_a_field_violation_with_the_capacity_rule(string kilograms)
    {
        var value = decimal.Parse(kilograms, System.Globalization.CultureInfo.InvariantCulture);
        var result = _validator.Validate(new RegisterVehicle("AB-1", VehicleType.Van, value));

        var violation = ValidationFailures.ToViolation(Assert.Single(result.Errors));
        Assert.Equal("capacity_kg", violation.FieldPath);
        Assert.Equal("VEHICLE_CAPACITY_MUST_BE_POSITIVE", violation.RuleCode);
        Assert.Equal("fleet.vehicle_capacity_must_be_positive", violation.Message.Key);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void A_type_outside_the_closed_list_is_a_field_violation(int type)
    {
        var result = _validator.Validate(new RegisterVehicle("AB-1", (VehicleType)type, 1m));

        var violation = ValidationFailures.ToViolation(Assert.Single(result.Errors));
        Assert.Equal("vehicle_type", violation.FieldPath);
    }

    [Fact]
    public void Every_problem_is_reported_at_once()
    {
        var result = _validator.Validate(new RegisterVehicle("", 0, 0m));

        Assert.Equal(3, result.Errors.Count);
    }
}

public sealed class ChangeVehicleStatusValidatorTests
{
    private readonly ChangeVehicleStatusValidator _validator = new();

    [Theory]
    [InlineData(OperationalStatus.Active)]
    [InlineData(OperationalStatus.Inactive)]
    public void Active_or_Inactive_passes(OperationalStatus status)
    {
        Assert.True(_validator.Validate(new ChangeVehicleStatus(Guid.CreateVersion7(), status)).IsValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Any_other_status_is_a_field_violation(int status)
    {
        var result = _validator.Validate(new ChangeVehicleStatus(Guid.CreateVersion7(), (OperationalStatus)status));

        Assert.Equal("status", ValidationFailures.ToViolation(Assert.Single(result.Errors)).FieldPath);
    }

    [Fact]
    public void An_empty_vehicle_id_is_a_field_violation()
    {
        var result = _validator.Validate(new ChangeVehicleStatus(Guid.Empty, OperationalStatus.Active));

        Assert.Equal("vehicle_id", ValidationFailures.ToViolation(Assert.Single(result.Errors)).FieldPath);
    }
}

public sealed class GetVehicleValidatorTests
{
    [Fact]
    public void An_empty_vehicle_id_is_a_field_violation()
    {
        var result = new GetVehicleValidator().Validate(new GetVehicle(Guid.Empty));

        Assert.Equal("vehicle_id", ValidationFailures.ToViolation(Assert.Single(result.Errors)).FieldPath);
    }

    [Fact]
    public void A_vehicle_id_passes()
    {
        Assert.True(new GetVehicleValidator().Validate(new GetVehicle(Guid.CreateVersion7())).IsValid);
    }
}
