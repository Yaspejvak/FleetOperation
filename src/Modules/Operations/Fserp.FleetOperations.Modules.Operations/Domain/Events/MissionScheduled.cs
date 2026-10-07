using MPCore.Domain.Events;

namespace Fserp.FleetOperations.Modules.Operations.Domain.Events;

/// <summary>
/// A mission was scheduled. In-process domain event, delivered after the commit. The scheduled time is
/// set once and never changed (O-1), so this event is raised at most once per mission.
/// </summary>
/// <param name="MissionId">The mission's identity.</param>
/// <param name="ScheduledAt">The time the mission is scheduled for.</param>
public sealed record MissionScheduled(Guid MissionId, DateTimeOffset ScheduledAt) : DomainEvent;
