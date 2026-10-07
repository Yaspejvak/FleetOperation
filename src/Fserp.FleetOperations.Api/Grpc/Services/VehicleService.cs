using System.Globalization;
using Fserp.FleetOperations.Modules.Fleet.Application.Queries;
using Fserp.FleetOperations.Modules.Fleet.Application.Views;
using Grpc.Core;
using MPCore.Application.Results;
using Wolverine;
using Proto = Fserp.FleetOperations.Api.Grpc.Fleet.V1;
using FleetContracts = Fserp.FleetOperations.Modules.Fleet.Contracts;
using FleetDomain = Fserp.FleetOperations.Modules.Fleet.Domain;

namespace Fserp.FleetOperations.Api.Grpc.Services;

/// <summary>
/// The Fleet read surface over gRPC (docs/architecture.md, "Transport parity"). A thin adapter: it builds
/// the same <see cref="GetVehicle"/> and <see cref="GetAvailableVehicles"/> query records the REST
/// endpoints build, sends them through the bus and maps the outcome onto the proto messages. No rule and
/// no query logic lives here.
/// </summary>
/// <remarks>
/// <para>
/// A failure returned by a query reaches the caller through <see cref="ResultExtensions.ValueOrThrow{T}"/>:
/// the <see cref="ResultFailureException"/> carries the very same <see cref="FailureDescriptor"/> the REST
/// path renders as Problem Details, so <c>VEHICLE_NOT_FOUND</c> is one failure answered on two transports
/// rather than two codes that can drift.
/// </para>
/// <para>
/// Proto enums travel as numbers by design, so the host's REST-only
/// <c>JsonStringEnumConverter(allowIntegerValues: false)</c> (F-9) does not apply here and no enum
/// converter is added. Capacities travel as decimal strings in invariant culture because protobuf has no
/// decimal type and a <c>double</c> would change the value.
/// </para>
/// </remarks>
/// <param name="bus">The message bus the queries are sent through.</param>
public sealed class VehicleService(IMessageBus bus) : global::Fserp.FleetOperations.Api.Grpc.Fleet.V1.VehicleService.VehicleServiceBase
{
    /// <summary>
    /// One vehicle by id. An unknown id answers <c>NOT_FOUND</c> under <c>fleet/VEHICLE_NOT_FOUND</c>.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="context">The server call context.</param>
    /// <remarks>
    /// A <c>vehicle_id</c> that is not a UUID parses to <see cref="Guid.Empty"/>, which
    /// <c>GetVehicleValidator</c>'s <c>NotEmpty()</c> refuses, so it answers as a validation failure (L-7).
    /// No code is fabricated here for an unparsable id.
    /// </remarks>
    public override async Task<Proto.GetVehicleResponse> GetVehicle(
        Proto.GetVehicleRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        _ = Guid.TryParse(request.VehicleId, out var vehicleId);
        var result = await bus
            .InvokeAsync<Result<VehicleView>>(new GetVehicle(vehicleId), context.CancellationToken)
            .ConfigureAwait(false);

        return new Proto.GetVehicleResponse { Vehicle = ToMessage(result.ValueOrThrow()) };
    }

    /// <summary>Every available vehicle, through the same cached query the REST endpoint sends.</summary>
    /// <param name="request">The request; it carries no field, because the list has no filter (F-7).</param>
    /// <param name="context">The server call context.</param>
    public override async Task<Proto.GetAvailableVehiclesResponse> GetAvailableVehicles(
        Proto.GetAvailableVehiclesRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var vehicles = await bus
            .InvokeAsync<IReadOnlyList<AvailableVehicleView>>(new GetAvailableVehicles(), context.CancellationToken)
            .ConfigureAwait(false);

        var response = new Proto.GetAvailableVehiclesResponse();
        response.Vehicles.AddRange(vehicles.Select(ToMessage));
        return response;
    }

    private static Proto.Vehicle ToMessage(VehicleView view)
    {
        var message = new Proto.Vehicle
        {
            VehicleId = view.Id.ToString(),
            PlateNumber = view.PlateNumber,
            VehicleType = ToMessage(view.VehicleType),
            CapacityKg = view.CapacityKg.ToString(CultureInfo.InvariantCulture),
            OperationalStatus = view.OperationalStatus switch
            {
                FleetDomain.OperationalStatus.Active => Proto.OperationalStatus.Active,
                FleetDomain.OperationalStatus.Inactive => Proto.OperationalStatus.Inactive,
                _ => Proto.OperationalStatus.Unspecified,
            },
            MaintenanceStatus = view.MaintenanceStatus switch
            {
                FleetDomain.MaintenanceStatus.NotUnderMaintenance => Proto.MaintenanceStatus.NotUnderMaintenance,
                FleetDomain.MaintenanceStatus.UnderMaintenance => Proto.MaintenanceStatus.UnderMaintenance,
                _ => Proto.MaintenanceStatus.Unspecified,
            },
            Status = view.DisplayStatus switch
            {
                FleetDomain.VehicleDisplayStatus.Active => Proto.VehicleStatus.Active,
                FleetDomain.VehicleDisplayStatus.Inactive => Proto.VehicleStatus.Inactive,
                FleetDomain.VehicleDisplayStatus.UnderMaintenance => Proto.VehicleStatus.UnderMaintenance,
                _ => Proto.VehicleStatus.Unspecified,
            },
        };

        // committed_mission_id is optional: absent, not empty text, when no mission holds the vehicle.
        if (view.CommittedMissionId is { } missionId)
        {
            message.CommittedMissionId = missionId.ToString();
        }

        return message;
    }

    private static Proto.AvailableVehicle ToMessage(AvailableVehicleView view) =>
        new()
        {
            VehicleId = view.Id.ToString(),
            PlateNumber = view.PlateNumber,
            VehicleType = ToMessage(view.VehicleType),
            CapacityKg = view.CapacityKg.ToString(CultureInfo.InvariantCulture),
        };

    private static Proto.VehicleType ToMessage(FleetContracts.VehicleType type) => type switch
    {
        FleetContracts.VehicleType.Van => Proto.VehicleType.Van,
        FleetContracts.VehicleType.Truck => Proto.VehicleType.Truck,
        FleetContracts.VehicleType.HeavyTruck => Proto.VehicleType.HeavyTruck,
        _ => Proto.VehicleType.Unspecified,
    };
}
