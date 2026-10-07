using MPCore.Domain.Events;

namespace Fserp.FleetOperations.Modules.Fleet.Domain.Events;

/// <summary>A vehicle was registered. In-process domain event, delivered after the commit.</summary>
/// <param name="VehicleId">The new vehicle's identity.</param>
public sealed record VehicleRegistered(Guid VehicleId) : DomainEvent;
