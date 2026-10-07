using MPCore.Domain.Events;

namespace Fserp.FleetOperations.Modules.Drivers.Domain.Events;

/// <summary>
/// A driver's operational status changed. In-process domain event, delivered after the commit. No handler
/// in this scope (docs/plans/drivers.md, "Domain events").
/// </summary>
/// <param name="DriverId">The driver's identity.</param>
/// <param name="From">The previous operational status.</param>
/// <param name="To">The new operational status.</param>
public sealed record DriverStatusChanged(Guid DriverId, OperationalStatus From, OperationalStatus To) : DomainEvent;
