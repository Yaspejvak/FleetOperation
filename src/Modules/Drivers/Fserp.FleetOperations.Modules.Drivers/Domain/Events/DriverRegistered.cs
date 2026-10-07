using MPCore.Domain.Events;

namespace Fserp.FleetOperations.Modules.Drivers.Domain.Events;

/// <summary>
/// A driver was registered. In-process domain event, delivered after the commit (docs/plans/drivers.md,
/// "Domain events").
/// </summary>
/// <remarks>
/// No handler in this scope: nothing is cached for drivers (D-6). The plan records that an unrouted event
/// is dropped with an informational log, and that this is accepted and must not be counted as tested
/// behaviour — so no test here asserts that it is delivered.
/// </remarks>
/// <param name="DriverId">The driver's identity.</param>
public sealed record DriverRegistered(Guid DriverId) : DomainEvent;
