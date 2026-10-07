namespace Fserp.FleetOperations.Modules.Operations.Domain;

/// <summary>
/// The edges of the mission state machine, written once (docs/plans/operations.md, "State machine").
/// Every transition method of <see cref="Mission"/> asks this table before it changes anything, so the
/// diagram and the code cannot drift.
/// </summary>
/// <remarks>
/// <see cref="MissionStatus.Completed"/> and <see cref="MissionStatus.Cancelled"/> appear as no edge's
/// source, which is what "terminal" means here: a terminal mission can never return to an active state.
/// <see cref="MissionStatus.InProgress"/> is deliberately absent from the sources of
/// <see cref="MissionTransition.Cancel"/> (O-3).
/// </remarks>
public static class MissionStateMachine
{
    private static readonly Dictionary<MissionTransition, (MissionStatus[] From, MissionStatus To)> Edges =
        new()
        {
            [MissionTransition.Schedule] = ([MissionStatus.Draft], MissionStatus.Scheduled),
            [MissionTransition.Assign] = ([MissionStatus.Scheduled], MissionStatus.Assigned),
            [MissionTransition.Start] = ([MissionStatus.Assigned], MissionStatus.InProgress),
            [MissionTransition.Complete] = ([MissionStatus.InProgress], MissionStatus.Completed),
            [MissionTransition.Cancel] =
                ([MissionStatus.Draft, MissionStatus.Scheduled, MissionStatus.Assigned], MissionStatus.Cancelled),
        };

    /// <summary>Whether the transition is an edge leaving the status.</summary>
    /// <param name="status">The mission's current status.</param>
    /// <param name="transition">The requested transition.</param>
    public static bool IsLegal(MissionStatus status, MissionTransition transition) =>
        Edges.TryGetValue(transition, out var edge) && Array.IndexOf(edge.From, status) >= 0;

    /// <summary>The status a transition leads to.</summary>
    /// <param name="transition">The requested transition.</param>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a member of the enum.</exception>
    public static MissionStatus TargetOf(MissionTransition transition) =>
        Edges.TryGetValue(transition, out var edge)
            ? edge.To
            : throw new ArgumentOutOfRangeException(nameof(transition), transition, "Not a member of MissionTransition.");
}
