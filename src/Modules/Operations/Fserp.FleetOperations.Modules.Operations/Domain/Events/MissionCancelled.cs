using MPCore.Domain.Events;

namespace Fserp.FleetOperations.Modules.Operations.Domain.Events;

/// <summary>
/// A mission was cancelled from Draft, Scheduled or Assigned (O-3). In-process domain event, delivered
/// after the commit. It carries no reason (O-10).
/// </summary>
/// <param name="MissionId">The mission's identity.</param>
/// <param name="From">The status the mission was cancelled from; only Assigned held resources.</param>
public sealed record MissionCancelled(Guid MissionId, MissionStatus From) : DomainEvent;
