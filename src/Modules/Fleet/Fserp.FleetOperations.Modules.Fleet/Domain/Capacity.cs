using System.Globalization;
using Fserp.FleetOperations.Modules.Fleet.Domain.Rules;
using MPCore.Domain.Model;
using MPCore.Domain.Rules;

namespace Fserp.FleetOperations.Modules.Fleet.Domain;

/// <summary>A vehicle's load capacity in kilograms: a positive decimal (X-4).</summary>
public sealed class Capacity : ValueObject
{
    private Capacity(decimal kilograms) => Kilograms = kilograms;

    /// <summary>The capacity in kilograms.</summary>
    public decimal Kilograms { get; }

    /// <summary>Validates a capacity given in kilograms.</summary>
    /// <param name="kilograms">The capacity in kilograms.</param>
    /// <exception cref="BusinessRuleValidationException"><c>VEHICLE_CAPACITY_MUST_BE_POSITIVE</c>.</exception>
    public static Capacity FromKilograms(decimal kilograms)
    {
        BusinessRules.Check(new CapacityMustBePositiveRule(kilograms));
        return new Capacity(kilograms);
    }

    /// <summary>The kilograms in invariant culture, which is also what the audit trail records.</summary>
    public override string ToString() => Kilograms.ToString(CultureInfo.InvariantCulture);

    /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        // decimal equality ignores scale (1.0 == 1.00), which is the intended meaning of "same capacity".
        yield return Kilograms;
    }
}
