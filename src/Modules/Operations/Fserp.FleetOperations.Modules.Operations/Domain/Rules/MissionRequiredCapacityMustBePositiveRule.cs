using MPCore.Domain.Rules;

namespace Fserp.FleetOperations.Modules.Operations.Domain.Rules;

/// <summary>
/// <c>MISSION_REQUIRED_CAPACITY_MUST_BE_POSITIVE</c>: the capacity a mission requires is zero or negative
/// kilograms (X-4). Checked by the <see cref="RequiredCapacity"/> value object.
/// </summary>
/// <param name="kilograms">The required capacity in kilograms.</param>
public sealed class MissionRequiredCapacityMustBePositiveRule(decimal kilograms)
    : BusinessRule(
        OperationsErrors.Domain,
        OperationsErrors.RequiredCapacityMustBePositive,
        OperationsErrors.MessageKey(OperationsErrors.RequiredCapacityMustBePositive))
{
    /// <inheritdoc />
    public override bool IsBroken() => kilograms <= 0m;
}
