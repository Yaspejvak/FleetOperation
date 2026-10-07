using MPCore.Domain.Rules;

namespace Fserp.FleetOperations.Modules.Operations.Domain.Rules;

/// <summary>
/// <c>MISSION_LOCATION_REQUIRED</c>: a location is empty after normalization (O-7). Checked by the
/// <see cref="Location"/> value object, so no mission can exist with an empty origin or destination.
/// </summary>
/// <param name="normalized">The location text, already trimmed.</param>
public sealed class MissionLocationRequiredRule(string? normalized)
    : BusinessRule(
        OperationsErrors.Domain,
        OperationsErrors.LocationRequired,
        OperationsErrors.MessageKey(OperationsErrors.LocationRequired))
{
    /// <inheritdoc />
    public override bool IsBroken() => string.IsNullOrEmpty(normalized);
}
