using Fserp.FleetOperations.Api.Security;
using Fserp.FleetOperations.Modules.Fleet.Application.Commands;
using Fserp.FleetOperations.Modules.Fleet.Application.Queries;
using Fserp.FleetOperations.Modules.Fleet.Application.Views;
using Fserp.FleetOperations.Modules.Fleet.Contracts;
using Fserp.FleetOperations.Modules.Fleet.Domain;
using MPCore.Application.Results;
using MPCore.Transport.Http;
using Wolverine;

namespace Fserp.FleetOperations.Api.Rest.Endpoints;

/// <summary>
/// The Fleet vehicle surface over REST, under the module's prefix <c>/api/fleet</c>. Each endpoint only
/// builds the command or query, sends it through the bus and maps the outcome; failures become Problem
/// Details (400 validation, 404 unknown vehicle, 409 plate already registered, 422 broken rule).
/// REST is the supported contract for the commands; the reads also get gRPC parity in round 4.
/// </summary>
public static class VehicleEndpoints
{
    /// <summary>The route name of <c>GET /api/fleet/vehicles/{vehicleId}</c>, used for the <c>201 Location</c> header.</summary>
    public const string GetVehicleRoute = "GetVehicle";

    /// <summary>The route name of <c>GET /api/fleet/vehicles/available</c>.</summary>
    public const string GetAvailableVehiclesRoute = "GetAvailableVehicles";

    /// <summary>Maps the vehicle endpoints.</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The group, so the host can bind it to the REST listener.</returns>
    public static RouteGroupBuilder MapVehicleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // The module's one route prefix; every Fleet route hangs below it.
        var group = endpoints.MapGroup("/api/fleet").WithTags("Fleet");

        group.MapPost("/vehicles", RegisterAsync)
            .WithName("RegisterVehicle")
            .RequireAuthorization(FleetOperationsPolicies.FleetManager);

        group.MapPut("/vehicles/{vehicleId:guid}/status", ChangeStatusAsync)
            .WithName("ChangeVehicleStatus")
            .RequireAuthorization(FleetOperationsPolicies.FleetManager);

        // Maintenance carries no data (F-4), so neither command has a body: the route value is the whole
        // input, and the actor is the validated token's.
        group.MapPost("/vehicles/{vehicleId:guid}/maintenance/start", StartMaintenanceAsync)
            .WithName("StartMaintenance")
            .RequireAuthorization(FleetOperationsPolicies.FleetManager);

        group.MapPost("/vehicles/{vehicleId:guid}/maintenance/complete", CompleteMaintenanceAsync)
            .WithName("CompleteMaintenance")
            .RequireAuthorization(FleetOperationsPolicies.FleetManager);

        // The :guid constraint keeps this route from matching /vehicles/available.
        group.MapGet("/vehicles/{vehicleId:guid}", GetAsync)
            .WithName(GetVehicleRoute)
            .RequireAuthorization(FleetOperationsPolicies.OperationalReader);

        group.MapGet("/vehicles/available", GetAvailableAsync)
            .WithName(GetAvailableVehiclesRoute)
            .RequireAuthorization(FleetOperationsPolicies.OperationalReader);

        return group;
    }

    private static async Task<IResult> RegisterAsync(
        RegisterVehicleRequest request,
        IMessageBus bus,
        CancellationToken cancellationToken)
    {
        // An absent plate number becomes empty and is refused by the validator with the plate's own code.
        var command = new RegisterVehicle(request.PlateNumber ?? string.Empty, request.VehicleType, request.CapacityKg);
        var result = await bus.InvokeAsync<Result<VehicleView>>(command, cancellationToken).ConfigureAwait(false);
        return result.ToHttpResult(view => Results.CreatedAtRoute(GetVehicleRoute, new { vehicleId = view.Id }, view));
    }

    private static async Task<IResult> ChangeStatusAsync(
        Guid vehicleId,
        ChangeVehicleStatusRequest request,
        IMessageBus bus,
        CancellationToken cancellationToken)
    {
        var command = new ChangeVehicleStatus(vehicleId, request.Status);
        var result = await bus.InvokeAsync<Result<VehicleView>>(command, cancellationToken).ConfigureAwait(false);
        return result.ToHttpResult(Results.Ok);
    }

    private static async Task<IResult> StartMaintenanceAsync(
        Guid vehicleId,
        IMessageBus bus,
        CancellationToken cancellationToken)
    {
        var command = new StartMaintenance(vehicleId);
        var result = await bus.InvokeAsync<Result<VehicleView>>(command, cancellationToken).ConfigureAwait(false);
        return result.ToHttpResult(Results.Ok);
    }

    private static async Task<IResult> CompleteMaintenanceAsync(
        Guid vehicleId,
        IMessageBus bus,
        CancellationToken cancellationToken)
    {
        var command = new CompleteMaintenance(vehicleId);
        var result = await bus.InvokeAsync<Result<VehicleView>>(command, cancellationToken).ConfigureAwait(false);
        return result.ToHttpResult(Results.Ok);
    }

    private static async Task<IResult> GetAsync(
        Guid vehicleId,
        IMessageBus bus,
        CancellationToken cancellationToken)
    {
        var result = await bus.InvokeAsync<Result<VehicleView>>(new GetVehicle(vehicleId), cancellationToken).ConfigureAwait(false);
        return result.ToHttpResult(Results.Ok);
    }

    private static async Task<IResult> GetAvailableAsync(IMessageBus bus, CancellationToken cancellationToken)
    {
        // The query returns the list itself: there is no failure case, so there is no failure to map.
        var vehicles = await bus
            .InvokeAsync<IReadOnlyList<AvailableVehicleView>>(new GetAvailableVehicles(), cancellationToken)
            .ConfigureAwait(false);
        return Results.Ok(vehicles);
    }
}

/// <summary>The body of <c>POST /api/fleet/vehicles</c>. Carries no actor: identity comes from the token.</summary>
/// <param name="PlateNumber">The plate number.</param>
/// <param name="VehicleType"><c>Van</c>, <c>Truck</c> or <c>HeavyTruck</c>.</param>
/// <param name="CapacityKg">The capacity in kilograms.</param>
public sealed record RegisterVehicleRequest(string? PlateNumber, VehicleType VehicleType, decimal CapacityKg);

/// <summary>The body of <c>PUT /api/fleet/vehicles/{vehicleId}/status</c>.</summary>
/// <param name="Status"><c>Active</c> or <c>Inactive</c>.</param>
public sealed record ChangeVehicleStatusRequest(OperationalStatus Status);
