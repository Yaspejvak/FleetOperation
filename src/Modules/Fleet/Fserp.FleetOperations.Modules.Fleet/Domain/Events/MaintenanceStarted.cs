using MPCore.Domain.Events;

namespace Fserp.FleetOperations.Modules.Fleet.Domain.Events;

/// <summary>
/// A vehicle entered maintenance. In-process domain event, delivered after the commit. Maintenance
/// carries no data (F-4), so the event names the vehicle only.
/// </summary>
/// <param name="VehicleId">The vehicle's identity.</param>
public sealed record MaintenanceStarted(Guid VehicleId) : DomainEvent;
