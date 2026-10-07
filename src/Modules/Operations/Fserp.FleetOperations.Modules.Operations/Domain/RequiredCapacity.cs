using System.Globalization;
using Fserp.FleetOperations.Modules.Operations.Domain.Rules;
using MPCore.Domain.Model;
using MPCore.Domain.Rules;

namespace Fserp.FleetOperations.Modules.Operations.Domain;

/// <summary>
/// The load a mission requires, in kilograms: a positive decimal (X-4). Operations' own type.
/// </summary>
/// <remarks>
/// It is deliberately <b>not</b> Fleet's <c>Capacity</c>, even though both are positive kilograms. Fleet's
/// type is the capacity a vehicle <em>has</em> and carries Fleet's rule code; this one is the capacity a
/// mission <em>needs</em> and carries Operations'. Sharing the type would put a Fleet Domain type inside
/// Operations' Domain, which the module map forbids (Operations may use Fleet.Contracts only), and would
/// report a mission's broken invariant under the <c>fleet</c> error domain.
/// </remarks>
public sealed class RequiredCapacity : ValueObject
{
    private RequiredCapacity(decimal kilograms) => Kilograms = kilograms;

    /// <summary>The required capacity in kilograms.</summary>
    public decimal Kilograms { get; }

    /// <summary>Validates a required capacity given in kilograms.</summary>
    /// <param name="kilograms">The required capacity in kilograms.</param>
    /// <exception cref="BusinessRuleValidationException"><c>MISSION_REQUIRED_CAPACITY_MUST_BE_POSITIVE</c>.</exception>
    public static RequiredCapacity FromKilograms(decimal kilograms)
    {
        BusinessRules.Check(new MissionRequiredCapacityMustBePositiveRule(kilograms));
        return new RequiredCapacity(kilograms);
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
