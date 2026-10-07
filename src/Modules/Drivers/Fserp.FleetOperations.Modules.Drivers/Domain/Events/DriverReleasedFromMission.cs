using MPCore.Domain.Events;

namespace Fserp.FleetOperations.Modules.Drivers.Domain.Events;

/// <summary>
/// A driver was released from the mission that held it. In-process domain event, delivered after the
/// commit. No handler in this scope (docs/plans/drivers.md, "Domain events").
/// </summary>
/// <param name="DriverId">The driver's identity.</param>
/// <param name="MissionId">The mission that held the driver.</param>
public sealed record DriverReleasedFromMission(Guid DriverId, Guid MissionId) : DomainEvent;
