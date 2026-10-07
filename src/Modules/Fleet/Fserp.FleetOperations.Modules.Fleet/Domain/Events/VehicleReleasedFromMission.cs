using MPCore.Domain.Events;

namespace Fserp.FleetOperations.Modules.Fleet.Domain.Events;

/// <summary>
/// A vehicle was released from the mission that held it. In-process domain event, delivered after the
/// commit (docs/plans/fleet.md, "Domain events").
/// </summary>
/// <remarks>
/// Raised by <see cref="Vehicle.ReleaseFromMission"/>, which Operations reaches through
/// <c>IVehicleCommitments</c> when a mission completes or is cancelled. It is routed to
/// <c>AvailableVehiclesCacheEvictionHandler</c>, because the release makes the vehicle available again
/// and the cached list must gain it (decision 4).
/// </remarks>
/// <param name="VehicleId">The vehicle's identity.</param>
/// <param name="MissionId">The mission that held the vehicle.</param>
public sealed record VehicleReleasedFromMission(Guid VehicleId, Guid MissionId) : DomainEvent;
