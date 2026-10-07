using Fserp.FleetOperations.Modules.Fleet.Application.Ports;
using Fserp.FleetOperations.Modules.Fleet.Application.Views;
using MPCore.Application.Messaging;
using MPCore.Application.Results;

namespace Fserp.FleetOperations.Modules.Fleet.Application.Queries;

/// <summary>Reads one vehicle. Not cached (decision 4).</summary>
/// <param name="VehicleId">The vehicle's identity.</param>
public sealed record GetVehicle(Guid VehicleId) : IQuery<Result<VehicleView>>;

/// <summary>Handles <see cref="GetVehicle"/>. Reads only: no unit of work, nothing published.</summary>
public static class GetVehicleHandler
{
    /// <summary>Returns the vehicle, or <c>404 VEHICLE_NOT_FOUND</c>.</summary>
    /// <param name="query">The query.</param>
    /// <param name="vehicles">The vehicle read model.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<Result<VehicleView>> Handle(
        GetVehicle query,
        IVehicleReadModel vehicles,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(vehicles);

        var view = await vehicles.GetAsync(query.VehicleId, cancellationToken).ConfigureAwait(false);
        return view is null
            ? Result<VehicleView>.FromFailure(FleetFailures.VehicleNotFound())
            : Result<VehicleView>.Success(view);
    }
}
