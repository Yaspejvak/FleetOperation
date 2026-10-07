using MPCore.Domain.Rules;

namespace Fserp.FleetOperations.Modules.Operations.Domain.Rules;

/// <summary>
/// <c>MISSION_INVALID_TRANSITION</c>: the requested transition is not an edge leaving the mission's
/// current status (docs/plans/operations.md, "State machine").
/// </summary>
/// <remarks>
/// The rule carries both arguments the plan names, so the rendered message can say where the mission is
/// and what was asked of it rather than only that something was refused.
/// </remarks>
/// <param name="current">The mission's current status.</param>
/// <param name="requested">The requested transition.</param>
public sealed class MissionInvalidTransitionRule(MissionStatus current, MissionTransition requested)
    : BusinessRule(
        OperationsErrors.Domain,
        OperationsErrors.InvalidTransition,
        OperationsErrors.MessageKey(OperationsErrors.InvalidTransition),
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["current_status"] = current.ToString(),
            ["requested_transition"] = requested.ToString(),
        })
{
    /// <inheritdoc />
    public override bool IsBroken() => !MissionStateMachine.IsLegal(current, requested);
}
