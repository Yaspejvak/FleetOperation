using MPCore.Domain.Events;

namespace Fserp.FleetOperations.Modules.Operations.Domain.Events;

/// <summary>
/// A mission was created, in <see cref="MissionStatus.Draft"/>. In-process domain event, delivered after
/// the commit (docs/plans/operations.md, "Domain events").
/// </summary>
/// <remarks>No handler in this scope: cache eviction is driven by Fleet's own events.</remarks>
/// <param name="MissionId">The mission's identity.</param>
public sealed record MissionCreated(Guid MissionId) : DomainEvent;
