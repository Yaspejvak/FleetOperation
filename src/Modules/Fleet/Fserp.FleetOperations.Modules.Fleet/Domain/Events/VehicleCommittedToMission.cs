using MPCore.Domain.Events;

namespace Fserp.FleetOperations.Modules.Fleet.Domain.Events;

/// <summary>
/// A vehicle was committed to a mission. In-process domain event, delivered after the commit
/// (docs/plans/fleet.md, "Domain events").
/// </summary>
/// <remarks>
/// Raised by <see cref="Vehicle.CommitToMission"/>, which Operations reaches through
/// <c>IVehicleCommitments</c> inside the Assign transaction (decision 1). It is routed to
/// <c>AvailableVehiclesCacheEvictionHandler</c>, because a commitment makes the vehicle unavailable and
/// the cached list must lose it (decision 4).
/// </remarks>
/// <param name="VehicleId">The vehicle's identity.</param>
/// <param name="MissionId">The mission holding the vehicle.</param>
public sealed record VehicleCommittedToMission(Guid VehicleId, Guid MissionId) : DomainEvent;
