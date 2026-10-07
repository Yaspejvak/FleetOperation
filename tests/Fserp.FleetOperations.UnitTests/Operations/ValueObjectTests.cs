using Fserp.FleetOperations.Modules.Operations.Domain;
using MPCore.Domain.Rules;

namespace Fserp.FleetOperations.UnitTests.Operations;

/// <summary>
/// <see cref="Location"/>: non-empty text after trimming, with no format and no length rule (O-7).
/// </summary>
public sealed class LocationTests
{
    [Theory]
    [InlineData("Tehran", "Tehran")]
    [InlineData("  Tehran  ", "Tehran")]
    [InlineData("\tWarehouse 4\n", "Warehouse 4")]
    public void A_location_is_trimmed_and_otherwise_kept_as_written(string entered, string expected)
    {
        Assert.Equal(expected, Location.Create(entered).Value);
    }

    [Fact]
    public void Case_is_preserved_because_a_location_is_read_by_a_human()
    {
        Assert.Equal("Tehran, Gate B", Location.Create("Tehran, Gate B").Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void An_empty_location_is_refused_with_the_modules_own_code(string? entered)
    {
        var refused = Assert.Throws<BusinessRuleValidationException>(() => Location.Create(entered));

        Assert.Equal("MISSION_LOCATION_REQUIRED", refused.Rule.Code);
        Assert.Equal("operations", refused.Rule.ErrorDomain);
    }

    [Fact]
    public void A_long_location_is_accepted_because_the_plan_names_no_length()
    {
        // The absence of a limit is deliberate: O-7 names one rule, and a bound nobody decided would be an
        // invented limit. The column is text for the same reason.
        var long_ = new string('x', 5_000);

        Assert.Equal(long_, Location.Create(long_).Value);
    }

    [Fact]
    public void Two_locations_with_the_same_normalized_text_are_equal()
    {
        Assert.Equal(Location.Create("Tehran"), Location.Create("  Tehran "));
        Assert.NotEqual(Location.Create("Tehran"), Location.Create("tehran"));
    }
}

/// <summary>
/// <see cref="RequiredCapacity"/>: positive kilograms (X-4), and Operations' own type rather than Fleet's.
/// </summary>
public sealed class RequiredCapacityTests
{
    [Theory]
    [InlineData("0.01")]
    [InlineData("800")]
    [InlineData("12500.75")]
    public void A_positive_capacity_is_kept_exactly(string kilograms)
    {
        var value = decimal.Parse(kilograms, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(value, RequiredCapacity.FromKilograms(value).Kilograms);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-0.01")]
    [InlineData("-1000")]
    public void A_zero_or_negative_capacity_is_refused_with_the_modules_own_code(string kilograms)
    {
        var value = decimal.Parse(kilograms, System.Globalization.CultureInfo.InvariantCulture);

        var refused = Assert.Throws<BusinessRuleValidationException>(() => RequiredCapacity.FromKilograms(value));

        Assert.Equal("MISSION_REQUIRED_CAPACITY_MUST_BE_POSITIVE", refused.Rule.Code);
        Assert.Equal("operations", refused.Rule.ErrorDomain);
    }

    [Fact]
    public void It_is_not_Fleets_capacity_type()
    {
        // The plan is explicit: Operations' OWN type. Sharing Fleet's would put a Fleet Domain type in
        // Operations' Domain and report a mission's broken invariant under the fleet error domain.
        Assert.Equal(
            "Fserp.FleetOperations.Modules.Operations.Domain",
            typeof(RequiredCapacity).Namespace);
        Assert.NotEqual(
            typeof(Fserp.FleetOperations.Modules.Fleet.Domain.Capacity).Assembly,
            typeof(RequiredCapacity).Assembly);
    }

    [Fact]
    public void Scale_does_not_change_the_capacity()
    {
        Assert.Equal(RequiredCapacity.FromKilograms(1000m), RequiredCapacity.FromKilograms(1000.00m));
    }

    [Fact]
    public void It_renders_in_invariant_culture_for_the_audit_trail_and_the_wire()
    {
        Assert.Equal("1250.5", RequiredCapacity.FromKilograms(1250.5m).ToString());
    }
}
