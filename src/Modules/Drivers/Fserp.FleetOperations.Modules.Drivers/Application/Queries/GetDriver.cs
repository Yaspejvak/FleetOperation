using Fserp.FleetOperations.Modules.Drivers.Application.Ports;
using Fserp.FleetOperations.Modules.Drivers.Application.Views;
using MPCore.Application.Messaging;
using MPCore.Application.Results;

namespace Fserp.FleetOperations.Modules.Drivers.Application.Queries;

/// <summary>Reads one driver. Not cached (D-6).</summary>
/// <param name="DriverId">The driver's identity.</param>
public sealed record GetDriver(Guid DriverId) : IQuery<Result<DriverView>>;

/// <summary>Handles <see cref="GetDriver"/>. Reads only: no unit of work, nothing published.</summary>
public static class GetDriverHandler
{
    /// <summary>Returns the driver, or <c>404 DRIVER_NOT_FOUND</c>.</summary>
    /// <param name="query">The query.</param>
    /// <param name="drivers">The driver read model.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<Result<DriverView>> Handle(
        GetDriver query,
        IDriverReadModel drivers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(drivers);

        var view = await drivers.GetAsync(query.DriverId, cancellationToken).ConfigureAwait(false);
        return view is null
            ? Result<DriverView>.FromFailure(DriversFailures.DriverNotFound())
            : Result<DriverView>.Success(view);
    }
}
