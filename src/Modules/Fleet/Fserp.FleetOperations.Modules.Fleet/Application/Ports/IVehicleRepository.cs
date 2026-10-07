using Fserp.FleetOperations.Modules.Fleet.Domain;
using MPCore.Persistence.Abstractions;

namespace Fserp.FleetOperations.Modules.Fleet.Application.Ports;

/// <summary>Loads and adds <see cref="Vehicle"/> aggregates for the Fleet commands. Implemented in Infrastructure.</summary>
public interface IVehicleRepository : IRepository<Vehicle, Guid>
{
    /// <summary>Whether a vehicle with this (normalized) plate number already exists.</summary>
    /// <param name="plateNumber">The plate number.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <remarks>
    /// A pre-check that gives the caller a clean <c>409</c>. It is not the guarantee: two concurrent
    /// registrations can both pass it, and the unique index then refuses the second at commit.
    /// </remarks>
    Task<bool> PlateNumberExistsAsync(PlateNumber plateNumber, CancellationToken cancellationToken);
}
