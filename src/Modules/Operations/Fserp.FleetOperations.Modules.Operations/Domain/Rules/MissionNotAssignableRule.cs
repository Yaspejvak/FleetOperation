using MPCore.Domain.Rules;

namespace Fserp.FleetOperations.Modules.Operations.Domain.Rules;

/// <summary>
/// <c>MISSION_NOT_ASSIGNABLE</c>: the mission is not <see cref="MissionStatus.Scheduled"/>, so no vehicle
/// and no driver may be committed to it (docs/architecture.md, decision 1, assignment precondition 1).
/// </summary>
/// <remarks>
/// Assign is the one transition the plan's invariant table gives its own code rather than
/// <c>MISSION_INVALID_TRANSITION</c>: it is precondition 1 of the eight, and a caller assigning a Draft,
/// Assigned, InProgress, Completed or Cancelled mission is told exactly that.
/// </remarks>
/// <param name="current">The mission's current status.</param>
public sealed class MissionNotAssignableRule(MissionStatus current)
    : BusinessRule(
        OperationsErrors.Domain,
        OperationsErrors.NotAssignable,
        OperationsErrors.MessageKey(OperationsErrors.NotAssignable),
        new Dictionary<string, string>(StringComparer.Ordinal) { ["current_status"] = current.ToString() })
{
    /// <inheritdoc />
    public override bool IsBroken() => current != MissionStatus.Scheduled;
}
