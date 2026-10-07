using MPCore.Domain.Events;

namespace Fserp.FleetOperations.Modules.Fleet.Domain.Events;

/// <summary>
/// A vehicle left maintenance. In-process domain event, delivered after the commit. Maintenance carries
/// no data (F-4), so the event names the vehicle only; the operational status is untouched (F-1).
/// </summary>
/// <param name="VehicleId">The vehicle's identity.</param>
public sealed record MaintenanceCompleted(Guid VehicleId) : DomainEvent;
