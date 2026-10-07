using Fserp.FleetOperations.Modules.Operations.Domain.Rules;
using MPCore.Domain.Model;
using MPCore.Domain.Rules;

namespace Fserp.FleetOperations.Modules.Operations.Domain;

/// <summary>
/// A mission's origin or destination: non-empty text after trimming, with no format rule (O-7). Origin
/// and destination may be equal; nothing here compares two locations.
/// </summary>
/// <remarks>
/// There is deliberately no maximum length. O-7 names one rule — non-empty — and a bound the plan does
/// not name would be an invented limit, refused at the database as a <c>500</c> rather than as a stated
/// failure. The column is therefore <c>text</c>.
/// </remarks>
public sealed class Location : ValueObject
{
    private Location(string value) => Value = value;

    /// <summary>The normalized location text.</summary>
    public string Value { get; }

    /// <summary>Normalizes and validates a location.</summary>
    /// <param name="value">The location as entered.</param>
    /// <exception cref="BusinessRuleValidationException"><c>MISSION_LOCATION_REQUIRED</c>.</exception>
    public static Location Create(string? value)
    {
        var normalized = Normalize(value);
        BusinessRules.Check(new MissionLocationRequiredRule(normalized));
        return new Location(normalized);
    }

    /// <summary>The normalization applied before any check or comparison: trim only.</summary>
    /// <param name="value">The location as entered.</param>
    /// <remarks>
    /// Case is kept: a location is free text a human reads, and upper-casing it would change what the
    /// caller wrote. Nothing in this module compares two locations, so no case-insensitive form is needed.
    /// </remarks>
    public static string Normalize(string? value) => (value ?? string.Empty).Trim();

    /// <summary>The normalized text, which is also what the audit trail records.</summary>
    public override string ToString() => Value;

    /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }
}
