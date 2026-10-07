using MPCore.Domain.Events;

namespace Fserp.FleetOperations.Modules.Operations.Domain.Events;

/// <summary>
/// A mission started. In-process domain event, delivered after the commit. Start touches neither Fleet nor
/// Drivers: the resources stay committed.
/// </summary>
/// <param name="MissionId">The mission's identity.</param>
public sealed record MissionStarted(Guid MissionId) : DomainEvent;
