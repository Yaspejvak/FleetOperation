namespace Fserp.FleetOperations.Modules.Operations.Domain;

/// <summary>
/// The six states of a mission, and no others (docs/plans/operations.md). Stored as a string, so the
/// partial-index predicate on <c>operations.missions</c> reads plainly.
/// </summary>
public enum MissionStatus
{
    /// <summary>Created, with no scheduled time yet (O-1).</summary>
    Draft = 0,

    /// <summary>A scheduled time is fixed; no vehicle and no driver are held yet.</summary>
    Scheduled = 1,

    /// <summary>A vehicle and a driver are committed to the mission.</summary>
    Assigned = 2,

    /// <summary>The mission has started; it may start before its scheduled time (O-6).</summary>
    InProgress = 3,

    /// <summary>Terminal: the mission finished and its resources were released.</summary>
    Completed = 4,

    /// <summary>Terminal: the mission was cancelled from Draft, Scheduled or Assigned (O-3).</summary>
    Cancelled = 5,
}
