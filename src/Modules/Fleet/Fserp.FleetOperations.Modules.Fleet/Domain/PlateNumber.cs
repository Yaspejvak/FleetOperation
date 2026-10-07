using Fserp.FleetOperations.Modules.Fleet.Domain.Rules;
using MPCore.Domain.Model;
using MPCore.Domain.Rules;

namespace Fserp.FleetOperations.Modules.Fleet.Domain;

/// <summary>
/// A vehicle's plate number: trimmed and upper-cased, never empty, at most <see cref="MaxLength"/>
/// characters after normalization. No format rule (F-2). Uniqueness
/// across the fleet is a property of the set of vehicles, not of one value, so it is enforced by the
/// unique index on <c>fleet.vehicles(plate_number)</c> and checked by Register Vehicle.
/// </summary>
public sealed class PlateNumber : ValueObject
{
    private PlateNumber(string value) => Value = value;

    /// <summary>
    /// The longest plate number accepted, in characters of the normalized value (owner decision, round 1
    /// closure). The <c>plate_number</c> column carries the same bound.
    /// </summary>
    public const int MaxLength = 16;

    /// <summary>The normalized plate number.</summary>
    public string Value { get; }

    /// <summary>Normalizes and validates a plate number.</summary>
    /// <param name="value">The plate number as entered.</param>
    /// <exception cref="BusinessRuleValidationException">
    /// <c>VEHICLE_PLATE_NUMBER_REQUIRED</c> or <c>VEHICLE_PLATE_NUMBER_TOO_LONG</c>.
    /// </exception>
    public static PlateNumber Create(string? value)
    {
        var normalized = Normalize(value);
        BusinessRules.Check(new PlateNumberRequiredRule(normalized));
        BusinessRules.Check(new PlateNumberTooLongRule(normalized));
        return new PlateNumber(normalized);
    }

    /// <summary>The normalization applied before any check or comparison: trim, then upper case (invariant).</summary>
    /// <param name="value">The plate number as entered.</param>
    public static string Normalize(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant();

    /// <summary>The normalized plate number, which is also what the audit trail records.</summary>
    public override string ToString() => Value;

    /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }
}
