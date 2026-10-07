using Fserp.FleetOperations.Modules.Drivers.Domain.Events;
using Fserp.FleetOperations.Modules.Drivers.Domain.Rules;
using Fserp.FleetOperations.Modules.Fleet.Contracts;
using MPCore.Domain.Model;
using MPCore.Domain.Rules;

namespace Fserp.FleetOperations.Modules.Drivers.Domain;

/// <summary>
/// A driver of the fleet: the full name (D-1), the operational status, the vehicle types the driver may
/// operate and the mission the driver is committed to, if any. Plan: docs/plans/drivers.md.
/// </summary>
/// <remarks>
/// Every state change goes through a method that checks its rules before anything changes; no property
/// has a public setter and the qualification list is exposed read-only. The optimistic-concurrency token
/// (PostgreSQL <c>xmin</c>) is mapped in Infrastructure and is not visible here; it covers the
/// qualifications too, which is why the qualification rules belong to this aggregate.
/// </remarks>
public sealed class Driver : AggregateRoot<Guid>
{
    private readonly List<Qualification> _qualifications = [];

    // For Entity Framework materialization only.
    private Driver()
    {
    }

    private Driver(Guid id, DriverName fullName)
        : base(id)
    {
        FullName = fullName;
        OperationalStatus = OperationalStatus.Active;
        CommittedMissionId = null;
    }

    /// <summary>The driver's full name. Personal data: masked in the audit trail.</summary>
    public DriverName FullName { get; private set; } = null!;

    /// <summary>Whether the driver is in service; changed only by <see cref="ChangeStatus"/>.</summary>
    public OperationalStatus OperationalStatus { get; private set; }

    /// <summary>The vehicle types the driver may operate; at least one (D-5), each at most once.</summary>
    public IReadOnlyCollection<Qualification> Qualifications => _qualifications;

    /// <summary>The mission holding the driver while that mission is <c>Assigned</c> or <c>InProgress</c>.</summary>
    public Guid? CommittedMissionId { get; private set; }

    /// <summary>
    /// Registers a driver. The driver starts <see cref="OperationalStatus.Active"/> and committed to no
    /// mission, with one qualification per vehicle type given.
    /// </summary>
    /// <param name="id">The new driver's identity (a version 7 UUID).</param>
    /// <param name="fullName">The full name, already validated by being constructed.</param>
    /// <param name="vehicleTypes">The vehicle types the driver may operate: at least one, each at most once.</param>
    /// <param name="now">
    /// The instant the identities of the qualifications take their timestamp from. It comes from
    /// <c>IClock</c> in the handler: this aggregate never reads a clock of its own.
    /// </param>
    /// <returns>The registered driver, with <c>DriverRegistered</c> raised.</returns>
    /// <exception cref="BusinessRuleValidationException">
    /// <c>DRIVER_QUALIFICATION_REQUIRED</c>, then <c>DRIVER_QUALIFICATION_DUPLICATE</c>. The order is fixed
    /// by L-19 and is unobservable: an empty list cannot contain a duplicate.
    /// </exception>
    public static Driver Register(
        Guid id,
        DriverName fullName,
        IReadOnlyList<VehicleType> vehicleTypes,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(fullName);
        ArgumentNullException.ThrowIfNull(vehicleTypes);
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A driver needs a non-empty identity.", nameof(id));
        }

        foreach (var vehicleType in vehicleTypes)
        {
            if (!Enum.IsDefined(vehicleType))
            {
                // The validator refuses an undefined type before the handler runs; reaching this is a defect.
                throw new ArgumentOutOfRangeException(nameof(vehicleTypes), vehicleType, "Not a member of VehicleType.");
            }
        }

        // Both rules are checked before the driver exists at all, so a refusal constructs nothing and
        // raises nothing. CheckRule is the base class's own static entry point, the same one every
        // instance method of this aggregate uses.
        CheckRule(new DriverQualificationRequiredRule(vehicleTypes));
        CheckRule(new DriverQualificationDuplicateRule(vehicleTypes));

        var driver = new Driver(id, fullName);
        foreach (var vehicleType in vehicleTypes)
        {
            driver._qualifications.Add(new Qualification(Guid.CreateVersion7(now), vehicleType));
        }

        driver.Raise(new DriverRegistered(id));
        return driver;
    }

    /// <summary>
    /// Sets the operational status. Setting the status the driver already has changes nothing and raises
    /// no event, exactly as in Fleet (F-8). A driver committed to a mission cannot be set
    /// <see cref="OperationalStatus.Inactive"/> (D-2).
    /// </summary>
    /// <param name="status">The requested operational status.</param>
    /// <returns><see langword="true"/> when the status changed; <see langword="false"/> for the no-op.</returns>
    /// <exception cref="BusinessRuleValidationException"><c>DRIVER_HAS_MISSION_COMMITMENT</c>.</exception>
    public bool ChangeStatus(OperationalStatus status)
    {
        if (!Enum.IsDefined(status))
        {
            // The validator refuses an undefined status before the handler runs; reaching this is a defect.
            throw new ArgumentOutOfRangeException(nameof(status), status, "Not a member of OperationalStatus.");
        }

        if (status == OperationalStatus)
        {
            return false;
        }

        if (status == OperationalStatus.Inactive)
        {
            CheckRule(new DriverHasMissionCommitmentRule(CommittedMissionId));
        }

        var from = OperationalStatus;
        OperationalStatus = status;
        Raise(new DriverStatusChanged(Id, from, status));
        return true;
    }

    /// <summary>
    /// Commits the driver to a mission. Called by Operations inside the Assign transaction through
    /// <c>IDriverCommitments</c> (docs/architecture.md, decision 1); it is not a command on the bus.
    /// </summary>
    /// <remarks>
    /// Committing the driver to the mission it already holds changes nothing and raises no event, the same
    /// treatment <c>Vehicle.CommitToMission</c> gives the mission it already holds. Both
    /// <c>DRIVER_NOT_AVAILABLE</c> and <c>VEHICLE_NOT_AVAILABLE</c> are worded "committed to a different
    /// mission", so the same-mission call is not a refusal; answering it with a second event and a second
    /// commitment of the same fact would be the only alternative.
    /// </remarks>
    /// <param name="missionId">The mission being assigned.</param>
    /// <param name="vehicleType">The type of the vehicle assigned to the same mission.</param>
    /// <returns><see langword="true"/> when the commitment changed; <see langword="false"/> for the no-op.</returns>
    /// <exception cref="BusinessRuleValidationException">
    /// <c>DRIVER_NOT_ACTIVE</c>, <c>DRIVER_NOT_AVAILABLE</c> or <c>DRIVER_NOT_QUALIFIED</c>, checked in the
    /// order of decision 1's preconditions 6, 7 and 8 and all before anything changes.
    /// </exception>
    public bool CommitToMission(Guid missionId, VehicleType vehicleType)
    {
        if (missionId == Guid.Empty)
        {
            throw new ArgumentException("A mission needs a non-empty identity.", nameof(missionId));
        }

        if (CommittedMissionId == missionId)
        {
            return false;
        }

        CheckRule(new DriverNotActiveRule(OperationalStatus));
        CheckRule(new DriverNotAvailableRule(CommittedMissionId, missionId));
        CheckRule(new DriverNotQualifiedRule(_qualifications, vehicleType));

        CommittedMissionId = missionId;
        Raise(new DriverCommittedToMission(Id, missionId));
        return true;
    }

    /// <summary>
    /// Releases the driver from the mission that holds it. Called by Operations through
    /// <c>IDriverCommitments</c> when a mission completes or is cancelled.
    /// </summary>
    /// <remarks>
    /// A driver that holds no mission, or holds a different one, is refused rather than answered as a
    /// no-op: silently accepting would let one mission release a driver another mission is holding.
    /// </remarks>
    /// <param name="missionId">The mission that held the driver.</param>
    /// <exception cref="BusinessRuleValidationException"><c>DRIVER_NOT_COMMITTED_TO_MISSION</c>.</exception>
    public void ReleaseFromMission(Guid missionId)
    {
        CheckRule(new DriverNotCommittedToMissionRule(CommittedMissionId, missionId));

        CommittedMissionId = null;
        Raise(new DriverReleasedFromMission(Id, missionId));
    }
}
