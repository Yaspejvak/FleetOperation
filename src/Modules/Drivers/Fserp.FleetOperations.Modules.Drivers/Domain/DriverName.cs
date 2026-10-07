using Fserp.FleetOperations.Modules.Drivers.Domain.Rules;
using MPCore.Domain.Model;
using MPCore.Domain.Rules;

namespace Fserp.FleetOperations.Modules.Drivers.Domain;

/// <summary>
/// A driver's full name, the only profile field (D-1): trimmed, never empty. No format rule and no
/// case folding — a person's name is not an identifier, so it is kept as written apart from the
/// surrounding whitespace.
/// </summary>
/// <remarks>
/// It is personal data. The audit policy masks it with <c>Redact</c> (docs/plans/drivers.md, "Business
/// audit"), and nothing in this class writes it to a log or a message.
/// </remarks>
public sealed class DriverName : ValueObject
{
    private DriverName(string value) => Value = value;

    /// <summary>
    /// The longest full name accepted, in characters of the normalized value (lead decision L-17). It is
    /// input shape, not an invariant: <c>RegisterDriverValidator</c> refuses a longer name with a
    /// <c>400</c> and the <c>full_name</c> column carries the same bound. The plan lists no length rule,
    /// so this class declares none.
    /// </summary>
    public const int MaxLength = 128;

    /// <summary>The normalized full name.</summary>
    public string Value { get; }

    /// <summary>Normalizes and validates a full name.</summary>
    /// <param name="value">The full name as entered.</param>
    /// <exception cref="BusinessRuleValidationException"><c>DRIVER_NAME_REQUIRED</c>.</exception>
    public static DriverName Create(string? value)
    {
        var normalized = Normalize(value);
        BusinessRules.Check(new DriverNameRequiredRule(normalized));
        return new DriverName(normalized);
    }

    /// <summary>The normalization applied before any check or comparison: trim, and nothing else.</summary>
    /// <param name="value">The full name as entered.</param>
    public static string Normalize(string? value) => (value ?? string.Empty).Trim();

    /// <summary>The normalized full name.</summary>
    public override string ToString() => Value;

    /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }
}
