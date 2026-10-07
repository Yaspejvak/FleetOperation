using Fserp.FleetOperations.Modules.Operations.Domain.Events;
using Fserp.FleetOperations.Modules.Operations.Domain.Rules;
using MPCore.Domain.Model;
using MPCore.Domain.Rules;

namespace Fserp.FleetOperations.Modules.Operations.Domain;

/// <summary>
/// A transportation mission: where it goes, what it must carry, when it is scheduled, which vehicle and
/// driver are committed to it, and where it stands in its lifecycle. Plan: docs/plans/operations.md.
/// </summary>
/// <remarks>
/// <para>
/// One method per transition, each asking <see cref="MissionStateMachine"/> before it changes anything, so
/// a refusal leaves the aggregate exactly as it was and raises no event. <see cref="Status"/> has no
/// setter outside this class, so no caller can place a mission in a state the diagram does not reach.
/// </para>
/// <para>
/// <see cref="AssignedVehicleId"/> and <see cref="AssignedDriverId"/> are ids and nothing more: no Fleet or
/// Drivers type enters this module's Domain. Whether those resources <em>may</em> be committed is decided
/// by their own aggregates, through the Contracts ports, inside the Assign transaction (decision 1).
/// </para>
/// <para>
/// The optimistic-concurrency token (PostgreSQL <c>xmin</c>) is mapped in Infrastructure and is not visible
/// here, as are the two unique partial indexes that keep one vehicle and one driver on one active mission.
/// </para>
/// </remarks>
public sealed class Mission : AggregateRoot<Guid>
{
    // For Entity Framework materialization only.
    private Mission()
    {
    }

    private Mission(Guid id, Location origin, Location destination, RequiredCapacity requiredCapacity)
        : base(id)
    {
        Origin = origin;
        Destination = destination;
        RequiredCapacity = requiredCapacity;
        Status = MissionStatus.Draft;
        ScheduledAt = null;
        AssignedVehicleId = null;
        AssignedDriverId = null;
    }

    /// <summary>Where the mission starts (O-7).</summary>
    public Location Origin { get; private set; } = null!;

    /// <summary>Where the mission ends; it may equal <see cref="Origin"/> (O-7).</summary>
    public Location Destination { get; private set; } = null!;

    /// <summary>The load the mission requires, in kilograms (X-4).</summary>
    public RequiredCapacity RequiredCapacity { get; private set; } = null!;

    /// <summary>
    /// When the mission is scheduled for. <see langword="null"/> in <see cref="MissionStatus.Draft"/>, set
    /// once by <see cref="Schedule"/> and never changed (O-1), because Schedule is the only writer and its
    /// only legal source status is Draft.
    /// </summary>
    public DateTimeOffset? ScheduledAt { get; private set; }

    /// <summary>The vehicle committed to the mission, if any. An id only.</summary>
    public Guid? AssignedVehicleId { get; private set; }

    /// <summary>The driver committed to the mission, if any. An id only.</summary>
    public Guid? AssignedDriverId { get; private set; }

    /// <summary>Where the mission stands. Changed only by the transition methods of this class.</summary>
    public MissionStatus Status { get; private set; }

    /// <summary>
    /// Creates a mission in <see cref="MissionStatus.Draft"/>, with no scheduled time (O-1) and no
    /// resources. The locations and the required capacity have already passed their own rules by being
    /// constructed.
    /// </summary>
    /// <param name="id">The new mission's identity (a version 7 UUID).</param>
    /// <param name="origin">Where the mission starts.</param>
    /// <param name="destination">Where the mission ends; it may equal <paramref name="origin"/>.</param>
    /// <param name="requiredCapacity">The load the mission requires.</param>
    public static Mission Create(Guid id, Location origin, Location destination, RequiredCapacity requiredCapacity)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(requiredCapacity);
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A mission needs a non-empty identity.", nameof(id));
        }

        var mission = new Mission(id, origin, destination, requiredCapacity);
        mission.Raise(new MissionCreated(id));
        return mission;
    }

    /// <summary>Fixes the time the mission is scheduled for. Draft -&gt; Scheduled.</summary>
    /// <param name="scheduledAt">The scheduled time.</param>
    /// <remarks>
    /// There is no rule that the time lies in the future (O-2), and a mission may start before it (O-6).
    /// Schedule leaves Draft only, so the time cannot be changed afterwards (O-1).
    /// </remarks>
    /// <exception cref="BusinessRuleValidationException"><c>MISSION_INVALID_TRANSITION</c>.</exception>
    public void Schedule(DateTimeOffset scheduledAt)
    {
        CheckRule(new MissionInvalidTransitionRule(Status, MissionTransition.Schedule));

        ScheduledAt = scheduledAt;
        Status = MissionStatus.Scheduled;
        Raise(new MissionScheduled(Id, scheduledAt));
    }

    /// <summary>
    /// Records the vehicle and the driver assigned to the mission. Scheduled -&gt; Assigned.
    /// </summary>
    /// <param name="vehicleId">The vehicle's identity.</param>
    /// <param name="driverId">The driver's identity.</param>
    /// <remarks>
    /// This is assignment precondition 1 and nothing else. Preconditions 2 to 8 belong to the Vehicle and
    /// the Driver and are checked by those aggregates when the handler commits them, in the same
    /// transaction (decision 1). There is no reassignment and no unassignment (O-5): an assigned mission
    /// leaves Assigned only by Start or Cancel, and neither of those re-enters this method.
    /// </remarks>
    /// <exception cref="BusinessRuleValidationException"><c>MISSION_NOT_ASSIGNABLE</c>.</exception>
    public void Assign(Guid vehicleId, Guid driverId)
    {
        if (vehicleId == Guid.Empty)
        {
            throw new ArgumentException("A vehicle needs a non-empty identity.", nameof(vehicleId));
        }

        if (driverId == Guid.Empty)
        {
            throw new ArgumentException("A driver needs a non-empty identity.", nameof(driverId));
        }

        CheckRule(new MissionNotAssignableRule(Status));

        AssignedVehicleId = vehicleId;
        AssignedDriverId = driverId;
        Status = MissionStatus.Assigned;
        Raise(new MissionAssigned(Id, vehicleId, driverId));
    }

    /// <summary>Starts the mission. Assigned -&gt; InProgress.</summary>
    /// <remarks>
    /// It may start before <see cref="ScheduledAt"/> (O-6), so the scheduled time is not read here. Start
    /// changes nothing in Fleet or Drivers: the vehicle and the driver stay committed.
    /// </remarks>
    /// <exception cref="BusinessRuleValidationException"><c>MISSION_INVALID_TRANSITION</c>.</exception>
    public void Start()
    {
        CheckRule(new MissionInvalidTransitionRule(Status, MissionTransition.Start));

        Status = MissionStatus.InProgress;
        Raise(new MissionStarted(Id));
    }

    /// <summary>Completes the mission. InProgress -&gt; Completed, which is terminal.</summary>
    /// <remarks>
    /// The vehicle and the driver are released by the handler, through both commitment ports, in the same
    /// transaction. The ids stay on the mission as the record of who carried it.
    /// </remarks>
    /// <exception cref="BusinessRuleValidationException"><c>MISSION_INVALID_TRANSITION</c>.</exception>
    public void Complete()
    {
        CheckRule(new MissionInvalidTransitionRule(Status, MissionTransition.Complete));

        Status = MissionStatus.Completed;
        Raise(new MissionCompleted(Id));
    }

    /// <summary>
    /// Cancels the mission. Draft, Scheduled or Assigned -&gt; Cancelled, which is terminal. An
    /// <see cref="MissionStatus.InProgress"/> mission cannot be cancelled (O-3).
    /// </summary>
    /// <returns>
    /// The status the mission was cancelled from, so the handler knows whether resources were held:
    /// only <see cref="MissionStatus.Assigned"/> releases anything (L-24).
    /// </returns>
    /// <remarks>No cancellation reason (O-10).</remarks>
    /// <exception cref="BusinessRuleValidationException"><c>MISSION_INVALID_TRANSITION</c>.</exception>
    public MissionStatus Cancel()
    {
        CheckRule(new MissionInvalidTransitionRule(Status, MissionTransition.Cancel));

        var from = Status;
        Status = MissionStatus.Cancelled;
        Raise(new MissionCancelled(Id, from));
        return from;
    }
}
