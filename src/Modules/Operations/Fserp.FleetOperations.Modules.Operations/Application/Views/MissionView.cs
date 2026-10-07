using Fserp.FleetOperations.Modules.Operations.Domain;

namespace Fserp.FleetOperations.Modules.Operations.Application.Views;

/// <summary>
/// One mission as a caller sees it (docs/plans/operations.md, "Queries"). It carries ids only: a client
/// that needs the plate reads Fleet, and a client that needs the driver's name reads Drivers.
/// </summary>
/// <param name="Id">The mission's identity.</param>
/// <param name="Origin">Where the mission starts.</param>
/// <param name="Destination">Where the mission ends.</param>
/// <param name="RequiredCapacityKg">The load the mission requires, in kilograms.</param>
/// <param name="ScheduledAt">When the mission is scheduled for; absent while Draft.</param>
/// <param name="Status">Where the mission stands.</param>
/// <param name="AssignedVehicleId">The assigned vehicle's identity, if any.</param>
/// <param name="AssignedDriverId">The assigned driver's identity, if any.</param>
public sealed record MissionView(
    Guid Id,
    string Origin,
    string Destination,
    decimal RequiredCapacityKg,
    DateTimeOffset? ScheduledAt,
    MissionStatus Status,
    Guid? AssignedVehicleId,
    Guid? AssignedDriverId)
{
    /// <summary>Projects an aggregate into its view.</summary>
    /// <param name="mission">The mission.</param>
    public static MissionView From(Mission mission)
    {
        ArgumentNullException.ThrowIfNull(mission);
        return new MissionView(
            mission.Id,
            mission.Origin.Value,
            mission.Destination.Value,
            mission.RequiredCapacity.Kilograms,
            mission.ScheduledAt,
            mission.Status,
            mission.AssignedVehicleId,
            mission.AssignedDriverId);
    }
}
