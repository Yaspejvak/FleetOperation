namespace Fserp.FleetOperations.Modules.Operations.Domain;

/// <summary>
/// The transitions a caller can request on a mission: one per method of <see cref="Mission"/> after
/// creation. It is the second argument of <c>MISSION_INVALID_TRANSITION</c>, so a refusal says both where
/// the mission is and what was asked of it.
/// </summary>
public enum MissionTransition
{
    /// <summary>Draft -&gt; Scheduled.</summary>
    Schedule = 0,

    /// <summary>Scheduled -&gt; Assigned. Refused by <c>MISSION_NOT_ASSIGNABLE</c>, not by this code.</summary>
    Assign = 1,

    /// <summary>Assigned -&gt; InProgress.</summary>
    Start = 2,

    /// <summary>InProgress -&gt; Completed.</summary>
    Complete = 3,

    /// <summary>Draft, Scheduled or Assigned -&gt; Cancelled.</summary>
    Cancel = 4,
}
