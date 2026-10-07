using MPCore.Domain.Events;

namespace Fserp.FleetOperations.Modules.Operations.Domain.Events;

/// <summary>
/// A vehicle and a driver were assigned to a mission. In-process domain event, delivered after the commit.
/// </summary>
/// <remarks>
/// The ids travel alone: no Fleet or Drivers type enters this module's Domain. Fleet's cache is evicted by
/// Fleet's own <c>VehicleCommittedToMission</c>, which the same transaction raises.
/// </remarks>
/// <param name="MissionId">The mission's identity.</param>
/// <param name="VehicleId">The assigned vehicle's identity.</param>
/// <param name="DriverId">The assigned driver's identity.</param>
public sealed record MissionAssigned(Guid MissionId, Guid VehicleId, Guid DriverId) : DomainEvent;
