using Fserp.FleetOperations.Modules.Fleet.Domain;
using MPCore.Domain.Rules;

namespace Fserp.FleetOperations.UnitTests.Fleet;

public sealed class PlateNumberTests
{
    [Theory]
    [InlineData(" ab-12 cd ", "AB-12 CD")]
    [InlineData("xyz9", "XYZ9")]
    [InlineData("\tTR 77\n", "TR 77")]
    public void Is_trimmed_and_upper_cased(string entered, string expected)
    {
        Assert.Equal(expected, PlateNumber.Create(entered).Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Empty_after_normalization_breaks_VEHICLE_PLATE_NUMBER_REQUIRED(string? entered)
    {
        var refused = Assert.Throws<BusinessRuleValidationException>(() => PlateNumber.Create(entered));

        Assert.Equal("fleet", refused.Rule.ErrorDomain);
        Assert.Equal("VEHICLE_PLATE_NUMBER_REQUIRED", refused.Rule.Code);
        Assert.Equal("fleet.vehicle_plate_number_required", refused.Rule.MessageKey);
    }

    [Fact]
    public void Sixteen_characters_are_accepted()
    {
        Assert.Equal("ABCDEFGHIJ-12345", PlateNumber.Create("abcdefghij-12345").Value);
    }

    [Theory]
    [InlineData("ABCDEFGHIJ-123456")]
    [InlineData("  ABCDEFGHIJ-123456  ")]
    public void Seventeen_characters_break_VEHICLE_PLATE_NUMBER_TOO_LONG(string entered)
    {
        var refused = Assert.Throws<BusinessRuleValidationException>(() => PlateNumber.Create(entered));

        Assert.Equal("fleet", refused.Rule.ErrorDomain);
        Assert.Equal("VEHICLE_PLATE_NUMBER_TOO_LONG", refused.Rule.Code);
        Assert.Equal("fleet.vehicle_plate_number_too_long", refused.Rule.MessageKey);
    }

    [Theory]
    [InlineData("    abcdefghij-12345    ")]
    [InlineData("\t\nABCDEFGHIJ-12345 \t")]
    public void Surrounding_whitespace_is_not_counted(string entered)
    {
        // The length is measured on the normalized value: 24 and 20 characters entered, 16 kept.
        Assert.Equal("ABCDEFGHIJ-12345", PlateNumber.Create(entered).Value);
    }

    [Fact]
    public void The_limit_is_sixteen()
    {
        Assert.Equal(16, PlateNumber.MaxLength);
    }

    [Fact]
    public void Has_no_format_rule()
    {
        // F-2: no format rule; any non-empty text is a plate number.
        Assert.Equal("!@#  漢字", PlateNumber.Create("!@#  漢字").Value);
    }

    [Fact]
    public void Two_plates_equal_after_normalization_are_the_same_value()
    {
        Assert.Equal(PlateNumber.Create("ab 1"), PlateNumber.Create("  AB 1 "));
        Assert.NotEqual(PlateNumber.Create("AB 1"), PlateNumber.Create("AB1"));
    }

    [Fact]
    public void Renders_as_its_normalized_value_for_the_audit_trail()
    {
        Assert.Equal("AB-1", PlateNumber.Create(" ab-1").ToString());
    }
}

public sealed class CapacityTests
{
    [Theory]
    [InlineData("0.001")]
    [InlineData("1")]
    [InlineData("1250.5")]
    [InlineData("79228162514264337593543950335")]
    public void A_positive_number_of_kilograms_is_a_capacity(string kilograms)
    {
        var value = decimal.Parse(kilograms, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(value, Capacity.FromKilograms(value).Kilograms);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-0.001")]
    [InlineData("-1000")]
    public void Zero_or_negative_breaks_VEHICLE_CAPACITY_MUST_BE_POSITIVE(string kilograms)
    {
        var value = decimal.Parse(kilograms, System.Globalization.CultureInfo.InvariantCulture);

        var refused = Assert.Throws<BusinessRuleValidationException>(() => Capacity.FromKilograms(value));

        Assert.Equal("fleet", refused.Rule.ErrorDomain);
        Assert.Equal("VEHICLE_CAPACITY_MUST_BE_POSITIVE", refused.Rule.Code);
        Assert.Equal("fleet.vehicle_capacity_must_be_positive", refused.Rule.MessageKey);
    }

    [Fact]
    public void Equal_kilograms_are_the_same_capacity_whatever_the_scale()
    {
        Assert.Equal(Capacity.FromKilograms(1.0m), Capacity.FromKilograms(1.00m));
    }

    [Fact]
    public void Renders_in_invariant_culture_for_the_audit_trail()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("1250.5", Capacity.FromKilograms(1250.5m).ToString());
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }
}
