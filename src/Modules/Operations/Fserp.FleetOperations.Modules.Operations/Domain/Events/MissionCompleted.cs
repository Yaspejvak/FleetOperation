using MPCore.Domain.Events;

namespace Fserp.FleetOperations.Modules.Operations.Domain.Events;

/// <summary>
/// A mission completed. In-process domain event, delivered after the commit. The vehicle and the driver
/// are released in the same transaction, by the handler calling both commitment ports.
/// </summary>
/// <param name="MissionId">The mission's identity.</param>
public sealed record MissionCompleted(Guid MissionId) : DomainEvent;
