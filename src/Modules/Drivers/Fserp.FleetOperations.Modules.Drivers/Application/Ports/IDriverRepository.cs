using Fserp.FleetOperations.Modules.Drivers.Domain;
using MPCore.Persistence.Abstractions;

namespace Fserp.FleetOperations.Modules.Drivers.Application.Ports;

/// <summary>
/// Loads and adds <see cref="Driver"/> aggregates for the Drivers commands and for
/// <c>IDriverCommitments</c>. Implemented in Infrastructure.
/// </summary>
/// <remarks>
/// A load through this port brings the qualifications with the driver: they are the driver's own state,
/// the rules read them, and the driver's concurrency token covers them.
/// </remarks>
public interface IDriverRepository : IRepository<Driver, Guid>;
