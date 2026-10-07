using MPCore.Domain.Events;

namespace Fserp.FleetOperations.Modules.Drivers.Domain.Events;

/// <summary>
/// A driver was committed to a mission. In-process domain event, delivered after the commit. No handler in
/// this scope (docs/plans/drivers.md, "Domain events").
/// </summary>
/// <remarks>
/// It carries the driver and the mission, mirroring Fleet's <c>VehicleCommittedToMission</c> (L-10). The
/// plan names the event but not its fields; the vehicle type the commitment was checked against is not
/// carried, because no reader exists to use it and the fact the event states is "this driver now holds
/// this mission".
/// </remarks>
/// <param name="DriverId">The driver's identity.</param>
/// <param name="MissionId">The mission holding the driver.</param>
public sealed record DriverCommittedToMission(Guid DriverId, Guid MissionId) : DomainEvent;
