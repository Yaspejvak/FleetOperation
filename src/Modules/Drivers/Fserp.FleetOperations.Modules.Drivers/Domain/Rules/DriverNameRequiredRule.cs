using MPCore.Domain.Rules;

namespace Fserp.FleetOperations.Modules.Drivers.Domain.Rules;

/// <summary><c>DRIVER_NAME_REQUIRED</c>: the full name is empty after normalization.</summary>
/// <remarks>
/// The only rule the name carries in the Domain. Its length bound is input shape, enforced by
/// <c>RegisterDriverValidator</c> and by the column (lead decision L-17); the plan lists no length
/// invariant, so none is invented here.
/// </remarks>
/// <param name="normalized">The full name, already trimmed and collapsed.</param>
public sealed class DriverNameRequiredRule(string? normalized)
    : BusinessRule(DriversErrors.Domain, DriversErrors.NameRequired, DriversErrors.MessageKey(DriversErrors.NameRequired))
{
    /// <inheritdoc />
    public override bool IsBroken() => string.IsNullOrEmpty(normalized);
}
