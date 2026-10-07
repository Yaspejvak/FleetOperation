using Fserp.FleetOperations.Modules.Operations.Domain;
using MPCore.Persistence.Abstractions;

namespace Fserp.FleetOperations.Modules.Operations.Application.Ports;

/// <summary>
/// Loads and adds <see cref="Mission"/> aggregates for the Operations commands. Implemented in
/// Infrastructure. It never saves: the middleware owns the unit of work.
/// </summary>
public interface IMissionRepository : IRepository<Mission, Guid>;
