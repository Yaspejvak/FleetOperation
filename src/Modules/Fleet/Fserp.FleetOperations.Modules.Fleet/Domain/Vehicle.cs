using Fserp.FleetOperations.Modules.Fleet.Contracts;
using Fserp.FleetOperations.Modules.Fleet.Domain.Events;
using Fserp.FleetOperations.Modules.Fleet.Domain.Rules;
using MPCore.Domain.Model;
using MPCore.Domain.Rules;

namespace Fserp.FleetOperations.Modules.Fleet.Domain;

/// <summary>
/// A vehicle of the fleet: its registration data, its two independent status fields (F-1) and the
/// mission it is committed to, if any. Plan: docs/plans/fleet.md.
/// </summary>
/// <remarks>
/// Every state change goes through a method that checks its rules before anything changes; no
/// property has a public setter. The optimistic-concurrency token (PostgreSQL <c>xmin</c>) is mapped in
/// Infrastructure and is not visible here.
/// </remarks>
public sealed class Vehicle : AggregateRoot<Guid>
{
    // For Entity Framework materialization only.
    private Vehicle()
    {
    }

    private Vehicle(Guid id, PlateNumber plateNumber, VehicleType vehicleType, Capacity capacity)
        : base(id)
    {
        PlateNumber = plateNumber;
        VehicleType = vehicleType;
        Capacity = capacity;
        OperationalStatus = OperationalStatus.Active;
        MaintenanceStatus = MaintenanceStatus.NotUnderMaintenance;
        CommittedMissionId = null;
    }

    /// <summary>The normalized plate number, unique across the fleet (F-2).</summary>
    public PlateNumber PlateNumber { get; private set; } = null!;

    /// <summary>The vehicle's type, from the closed list in Fleet.Contracts (X-5).</summary>
    public VehicleType VehicleType { get; private set; }

    /// <summary>The load capacity in kilograms (X-4).</summary>
    public Capacity Capacity { get; private set; } = null!;

    /// <summary>Whether the vehicle is in operation; changed only by <see cref="ChangeStatus"/>.</summary>
    public OperationalStatus OperationalStatus { get; private set; }

    /// <summary>Whether the vehicle is under maintenance; changed only by the maintenance commands.</summary>
    public MaintenanceStatus MaintenanceStatus { get; private set; }

    /// <summary>The mission holding the vehicle while that mission is <c>Assigned</c> or <c>InProgress</c>.</summary>
    public Guid? CommittedMissionId { get; private set; }

    /// <summary>The derived status shown to readers: under maintenance wins, otherwise the operational status.</summary>
    public VehicleDisplayStatus DisplayStatus =>
        MaintenanceStatus == MaintenanceStatus.UnderMaintenance
            ? VehicleDisplayStatus.UnderMaintenance
            : OperationalStatus == OperationalStatus.Active
                ? VehicleDisplayStatus.Active
                : VehicleDisplayStatus.Inactive;

    /// <summary>
    /// Registers a vehicle. It starts <see cref="OperationalStatus.Active"/> (F-5),
    /// <see cref="MaintenanceStatus.NotUnderMaintenance"/> and committed to no mission. The plate number
    /// and the capacity have already passed their own rules by being constructed.
    /// </summary>
    /// <param name="id">The new vehicle's identity (a version 7 UUID).</param>
    /// <param name="plateNumber">The plate number.</param>
    /// <param name="vehicleType">The vehicle type.</param>
    /// <param name="capacity">The capacity.</param>
    public static Vehicle Register(Guid id, PlateNumber plateNumber, VehicleType vehicleType, Capacity capacity)
    {
        ArgumentNullException.ThrowIfNull(plateNumber);
        ArgumentNullException.ThrowIfNull(capacity);
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A vehicle needs a non-empty identity.", nameof(id));
        }

        if (!Enum.IsDefined(vehicleType))
        {
            // The validator refuses an undefined type before the handler runs; reaching this is a defect.
            throw new ArgumentOutOfRangeException(nameof(vehicleType), vehicleType, "Not a member of VehicleType.");
        }

        var vehicle = new Vehicle(id, plateNumber, vehicleType, capacity);
        vehicle.Raise(new VehicleRegistered(id));
        return vehicle;
    }

    /// <summary>
    /// Sets the operational status. Setting the status the vehicle already has changes nothing and
    /// raises no event (F-8). A vehicle committed to a mission cannot be set
    /// <see cref="OperationalStatus.Inactive"/> (F-3).
    /// </summary>
    /// <param name="status">The requested operational status.</param>
    /// <returns><see langword="true"/> when the status changed; <see langword="false"/> for the no-op.</returns>
    /// <exception cref="BusinessRuleValidationException"><c>VEHICLE_HAS_MISSION_COMMITMENT</c>.</exception>
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
            CheckRule(new VehicleHasMissionCommitmentRule(CommittedMissionId));
        }

        var from = OperationalStatus;
        OperationalStatus = status;
        Raise(new VehicleStatusChanged(Id, from, status));
        return true;
    }

    /// <summary>
    /// Puts the vehicle under maintenance. Both rules are checked before anything changes, so a refusal
    /// leaves the aggregate exactly as it was and raises no event.
    /// </summary>
    /// <remarks>
    /// There is no no-op path: starting maintenance twice is refused (unlike <see cref="ChangeStatus"/>,
    /// F-8). The operational status is not read: an <see cref="OperationalStatus.Inactive"/> vehicle may
    /// start maintenance (F-4), and only the maintenance field changes (F-1).
    /// </remarks>
    /// <exception cref="BusinessRuleValidationException">
    /// <c>VEHICLE_ALREADY_UNDER_MAINTENANCE</c>, or <c>VEHICLE_HAS_MISSION_COMMITMENT</c> when a mission
    /// holds the vehicle (decision 3). The order is fixed by L-5 and is unobservable: the two states
    /// cannot both hold in a consistent aggregate.
    /// </exception>
    public void StartMaintenance()
    {
        CheckRule(new VehicleAlreadyUnderMaintenanceRule(MaintenanceStatus));
        CheckRule(new VehicleHasMissionCommitmentRule(CommittedMissionId));

        MaintenanceStatus = MaintenanceStatus.UnderMaintenance;
        Raise(new MaintenanceStarted(Id));
    }

    /// <summary>
    /// Takes the vehicle out of maintenance. The rule is checked before anything changes, so a refusal
    /// leaves the aggregate exactly as it was and raises no event.
    /// </summary>
    /// <remarks>
    /// Only the maintenance field changes; the operational status is untouched (F-1). A vehicle that is
    /// not under maintenance is refused rather than answered as a no-op.
    /// </remarks>
    /// <exception cref="BusinessRuleValidationException"><c>VEHICLE_NOT_UNDER_MAINTENANCE</c>.</exception>
    public void CompleteMaintenance()
    {
        CheckRule(new VehicleNotUnderMaintenanceRule(MaintenanceStatus));

        MaintenanceStatus = MaintenanceStatus.NotUnderMaintenance;
        Raise(new MaintenanceCompleted(Id));
    }

    /// <summary>
    /// Commits the vehicle to a mission. Called by Operations inside the Assign transaction through
    /// <c>IVehicleCommitments</c> (docs/architecture.md, decision 1); it is not a command on the bus.
    /// </summary>
    /// <remarks>
    /// Committing the vehicle to the mission it already holds changes nothing and raises no event
    /// (docs/plans/fleet.md), exactly as <see cref="ChangeStatus"/> treats the status it already has. That
    /// path is taken before any rule is read, so a repeat of an assignment that already succeeded cannot
    /// be refused by a value the mission could not have changed meanwhile.
    /// <para>
    /// The four rules are the assignment preconditions 2 to 5 of decision 1, checked in that table's order
    /// and all before anything changes, so a refusal leaves the aggregate exactly as it was.
    /// </para>
    /// </remarks>
    /// <param name="missionId">The mission being assigned.</param>
    /// <param name="requiredCapacityKg">The capacity the mission requires, in kilograms (X-4).</param>
    /// <returns><see langword="true"/> when the commitment changed; <see langword="false"/> for the no-op.</returns>
    /// <exception cref="BusinessRuleValidationException">
    /// <c>VEHICLE_NOT_ACTIVE</c>, <c>VEHICLE_UNDER_MAINTENANCE</c>, <c>VEHICLE_CAPACITY_INSUFFICIENT</c> or
    /// <c>VEHICLE_NOT_AVAILABLE</c>.
    /// </exception>
    public bool CommitToMission(Guid missionId, decimal requiredCapacityKg)
    {
        if (missionId == Guid.Empty)
        {
            throw new ArgumentException("A mission needs a non-empty identity.", nameof(missionId));
        }

        if (CommittedMissionId == missionId)
        {
            return false;
        }

        CheckRule(new VehicleNotActiveRule(OperationalStatus));
        CheckRule(new VehicleUnderMaintenanceRule(MaintenanceStatus));
        CheckRule(new VehicleCapacityInsufficientRule(Capacity.Kilograms, requiredCapacityKg));
        CheckRule(new VehicleNotAvailableRule(CommittedMissionId, missionId));

        CommittedMissionId = missionId;
        Raise(new VehicleCommittedToMission(Id, missionId));
        return true;
    }

    /// <summary>
    /// Releases the vehicle from the mission that holds it. Called by Operations through
    /// <c>IVehicleCommitments</c> when a mission completes or is cancelled.
    /// </summary>
    /// <remarks>
    /// A vehicle that holds no mission, or holds a different one, is refused rather than answered as a
    /// no-op: silently accepting would let one mission release a vehicle another mission is holding.
    /// </remarks>
    /// <param name="missionId">The mission that held the vehicle.</param>
    /// <exception cref="BusinessRuleValidationException"><c>VEHICLE_NOT_COMMITTED_TO_MISSION</c>.</exception>
    public void ReleaseFromMission(Guid missionId)
    {
        CheckRule(new VehicleNotCommittedToMissionRule(CommittedMissionId, missionId));

        CommittedMissionId = null;
        Raise(new VehicleReleasedFromMission(Id, missionId));
    }
}
