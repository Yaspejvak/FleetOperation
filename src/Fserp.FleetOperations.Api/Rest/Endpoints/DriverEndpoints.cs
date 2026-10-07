using Fserp.FleetOperations.Api.Security;
using Fserp.FleetOperations.Modules.Drivers.Application.Commands;
using Fserp.FleetOperations.Modules.Drivers.Application.Queries;
using Fserp.FleetOperations.Modules.Drivers.Application.Views;
using Fserp.FleetOperations.Modules.Drivers.Domain;
using Fserp.FleetOperations.Modules.Fleet.Contracts;
using MPCore.Application.Results;
using MPCore.Transport.Http;
using Wolverine;

namespace Fserp.FleetOperations.Api.Rest.Endpoints;

/// <summary>
/// The Drivers surface over REST, under the module's prefix <c>/api/drivers</c>. Each endpoint only builds
/// the command or query, sends it through the bus and maps the outcome; failures become Problem Details
/// (400 validation, 404 unknown driver, 409 lost concurrency, 422 broken rule).
/// </summary>
/// <remarks>
/// REST is the whole contract for this module: docs/plans/drivers.md says "gRPC: none" for both queries,
/// so no proto and no gRPC service exists for drivers. The module prefix and the resource coincide
/// (<c>/api/drivers</c>), deliberately.
/// </remarks>
public static class DriverEndpoints
{
    /// <summary>The route name of <c>GET /api/drivers/{driverId}</c>, used for the <c>201 Location</c> header.</summary>
    public const string GetDriverRoute = "GetDriver";

    /// <summary>The route name of <c>GET /api/drivers/available</c>.</summary>
    public const string GetAvailableDriversRoute = "GetAvailableDrivers";

    /// <summary>Maps the driver endpoints.</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The group, so the host can bind it to the REST listener.</returns>
    public static RouteGroupBuilder MapDriverEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // The module's one route prefix; every Drivers route hangs below it.
        var group = endpoints.MapGroup("/api/drivers").WithTags("Drivers");

        group.MapPost("/", RegisterAsync)
            .WithName("RegisterDriver")
            .RequireAuthorization(FleetOperationsPolicies.FleetManager);

        group.MapPut("/{driverId:guid}/status", ChangeStatusAsync)
            .WithName("ChangeDriverStatus")
            .RequireAuthorization(FleetOperationsPolicies.FleetManager);

        // The :guid constraint keeps this route from matching /api/drivers/available.
        group.MapGet("/{driverId:guid}", GetAsync)
            .WithName(GetDriverRoute)
            .RequireAuthorization(FleetOperationsPolicies.OperationalReader);

        group.MapGet("/available", GetAvailableAsync)
            .WithName(GetAvailableDriversRoute)
            .RequireAuthorization(FleetOperationsPolicies.OperationalReader);

        return group;
    }

    private static async Task<IResult> RegisterAsync(
        RegisterDriverRequest request,
        IMessageBus bus,
        CancellationToken cancellationToken)
    {
        // An absent name becomes empty and an absent list becomes empty: the validator refuses both with
        // the module's own codes, rather than the endpoint deciding anything.
        var command = new RegisterDriver(request.FullName ?? string.Empty, request.VehicleTypes ?? []);
        var result = await bus.InvokeAsync<Result<DriverView>>(command, cancellationToken).ConfigureAwait(false);
        return result.ToHttpResult(view => Results.CreatedAtRoute(GetDriverRoute, new { driverId = view.Id }, view));
    }

    private static async Task<IResult> ChangeStatusAsync(
        Guid driverId,
        ChangeDriverStatusRequest request,
        IMessageBus bus,
        CancellationToken cancellationToken)
    {
        var command = new ChangeDriverStatus(driverId, request.Status);
        var result = await bus.InvokeAsync<Result<DriverView>>(command, cancellationToken).ConfigureAwait(false);
        return result.ToHttpResult(Results.Ok);
    }

    private static async Task<IResult> GetAsync(
        Guid driverId,
        IMessageBus bus,
        CancellationToken cancellationToken)
    {
        var result = await bus.InvokeAsync<Result<DriverView>>(new GetDriver(driverId), cancellationToken).ConfigureAwait(false);
        return result.ToHttpResult(Results.Ok);
    }

    private static async Task<IResult> GetAvailableAsync(IMessageBus bus, CancellationToken cancellationToken)
    {
        // The query returns the list itself: there is no failure case, so there is no failure to map.
        var drivers = await bus
            .InvokeAsync<IReadOnlyList<AvailableDriverView>>(new GetAvailableDrivers(), cancellationToken)
            .ConfigureAwait(false);
        return Results.Ok(drivers);
    }
}

/// <summary>The body of <c>POST /api/drivers</c>. Carries no actor: identity comes from the token.</summary>
/// <param name="FullName">The driver's full name.</param>
/// <param name="VehicleTypes">The vehicle types the driver may operate: <c>Van</c>, <c>Truck</c>, <c>HeavyTruck</c>.</param>
public sealed record RegisterDriverRequest(string? FullName, IReadOnlyList<VehicleType>? VehicleTypes);

/// <summary>The body of <c>PUT /api/drivers/{driverId}/status</c>.</summary>
/// <param name="Status"><c>Active</c> or <c>Inactive</c>.</param>
public sealed record ChangeDriverStatusRequest(OperationalStatus Status);
