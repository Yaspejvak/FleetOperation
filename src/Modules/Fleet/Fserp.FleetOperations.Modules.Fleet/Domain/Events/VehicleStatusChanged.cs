using MPCore.Domain.Events;

namespace Fserp.FleetOperations.Modules.Fleet.Domain.Events;

/// <summary>A vehicle's operational status changed. In-process domain event, delivered after the commit.</summary>
/// <param name="VehicleId">The vehicle's identity.</param>
/// <param name="From">The previous operational status.</param>
/// <param name="To">The new operational status.</param>
public sealed record VehicleStatusChanged(Guid VehicleId, OperationalStatus From, OperationalStatus To) : DomainEvent;
