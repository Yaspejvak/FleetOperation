using Fserp.FleetOperations.Api.Hosting;
using Fserp.FleetOperations.Api.Security;
using Fserp.FleetOperations.Modules.Operations.Application.Commands;
using Fserp.FleetOperations.Modules.Operations.Application.Queries;
using Fserp.FleetOperations.Modules.Operations.Application.Views;
using MPCore.Application.Querying;
using MPCore.Application.Results;
using MPCore.Transport.Http;
using Wolverine;

namespace Fserp.FleetOperations.Api.Rest.Endpoints;

/// <summary>
/// The Operations mission surface over REST, under the module's prefix <c>/api/operations</c>. Each
/// endpoint only builds the command or query, sends it through the bus and maps the outcome; failures
/// become Problem Details (400 validation, 404 unknown mission, vehicle or driver, 409 lost race, 422
/// broken rule).
/// </summary>
/// <remarks>
/// The six commands are REST only. The two reads also exist over gRPC
/// (<c>Grpc/Services/MissionService.cs</c>), and both transports build the very same query records sent
/// here, so the surfaces cannot drift.
/// </remarks>
public static class MissionEndpoints
{
    /// <summary>The route name of <c>GET /api/operations/missions/{missionId}</c>, used for the <c>201 Location</c> header.</summary>
    public const string GetMissionRoute = "GetMission";

    /// <summary>The route name of <c>GET /api/operations/missions/active</c>.</summary>
    public const string GetActiveMissionsRoute = "GetActiveMissions";

    /// <summary>Maps the mission endpoints.</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The group, so the host can bind it to the REST listener.</returns>
    public static RouteGroupBuilder MapMissionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // The module's one route prefix; every Operations route hangs below it.
        var group = endpoints.MapGroup("/api/operations").WithTags("Operations");

        group.MapPost("/missions", CreateAsync)
            .WithName("CreateMission")
            .RequireAuthorization(FleetOperationsPolicies.Operator);

        group.MapPost("/missions/{missionId:guid}/schedule", ScheduleAsync)
            .WithName("ScheduleMission")
            .RequireAuthorization(FleetOperationsPolicies.Operator);

        group.MapPost("/missions/{missionId:guid}/assign", AssignAsync)
            .WithName("AssignMission")
            .RequireAuthorization(FleetOperationsPolicies.Operator);

        // Start, Complete and Cancel carry no data, so the route value is the whole input (O-10 removes
        // the only field Cancel could have had).
        group.MapPost("/missions/{missionId:guid}/start", StartAsync)
            .WithName("StartMission")
            .RequireAuthorization(FleetOperationsPolicies.Operator);

        group.MapPost("/missions/{missionId:guid}/complete", CompleteAsync)
            .WithName("CompleteMission")
            .RequireAuthorization(FleetOperationsPolicies.Operator);

        group.MapPost("/missions/{missionId:guid}/cancel", CancelAsync)
            .WithName("CancelMission")
            .RequireAuthorization(FleetOperationsPolicies.Operator);

        // The :guid constraint keeps this route from matching /missions/active.
        group.MapGet("/missions/{missionId:guid}", GetAsync)
            .WithName(GetMissionRoute)
            .RequireAuthorization(FleetOperationsPolicies.OperationalReader);

        group.MapGet("/missions/active", GetActiveAsync)
            .WithName(GetActiveMissionsRoute)
            .RequireAuthorization(FleetOperationsPolicies.OperationalReader);

        return group;
    }

    private static async Task<IResult> CreateAsync(
        CreateMissionRequest request,
        IMessageBus bus,
        CancellationToken cancellationToken)
    {
        // Absent text becomes empty and is refused by the validator with the location's own code.
        var command = new CreateMission(
            request.Origin ?? string.Empty,
            request.Destination ?? string.Empty,
            request.RequiredCapacityKg);
        var result = await bus.InvokeAsync<Result<MissionView>>(command, cancellationToken).ConfigureAwait(false);
        return result.ToHttpResult(view => Results.CreatedAtRoute(GetMissionRoute, new { missionId = view.Id }, view));
    }

    private static async Task<IResult> ScheduleAsync(
        Guid missionId,
        ScheduleMissionRequest request,
        IMessageBus bus,
        CancellationToken cancellationToken)
    {
        var command = new ScheduleMission(missionId, request.ScheduledAt);
        var result = await bus.InvokeAsync<Result<MissionView>>(command, cancellationToken).ConfigureAwait(false);
        return result.ToHttpResult(Results.Ok);
    }

    private static async Task<IResult> AssignAsync(
        Guid missionId,
        AssignMissionRequest request,
        IMessageBus bus,
        CancellationToken cancellationToken)
    {
        var command = new AssignMission(missionId, request.VehicleId, request.DriverId);
        var result = await bus.InvokeAsync<Result<MissionView>>(command, cancellationToken).ConfigureAwait(false);
        return result.ToHttpResult(Results.Ok);
    }

    private static async Task<IResult> StartAsync(Guid missionId, IMessageBus bus, CancellationToken cancellationToken)
    {
        var result = await bus
            .InvokeAsync<Result<MissionView>>(new StartMission(missionId), cancellationToken)
            .ConfigureAwait(false);
        return result.ToHttpResult(Results.Ok);
    }

    private static async Task<IResult> CompleteAsync(Guid missionId, IMessageBus bus, CancellationToken cancellationToken)
    {
        var result = await bus
            .InvokeAsync<Result<MissionView>>(new CompleteMission(missionId), cancellationToken)
            .ConfigureAwait(false);
        return result.ToHttpResult(Results.Ok);
    }

    private static async Task<IResult> CancelAsync(Guid missionId, IMessageBus bus, CancellationToken cancellationToken)
    {
        var result = await bus
            .InvokeAsync<Result<MissionView>>(new CancelMission(missionId), cancellationToken)
            .ConfigureAwait(false);
        return result.ToHttpResult(Results.Ok);
    }

    private static async Task<IResult> GetAsync(Guid missionId, IMessageBus bus, CancellationToken cancellationToken)
    {
        var result = await bus
            .InvokeAsync<Result<MissionView>>(new GetMission(missionId), cancellationToken)
            .ConfigureAwait(false);
        return result.ToHttpResult(Results.Ok);
    }

    private static async Task<IResult> GetActiveAsync(
        int? page,
        int? pageSize,
        IMessageBus bus,
        CancellationToken cancellationToken)
    {
        // PageRequests.From decides what "not chosen" means, identically on both transports, and
        // PageRequest bounds the rest: page 0 or a request for a million rows is a bounded page rather
        // than a failure or an outage. The endpoint decides nothing about either.
        var query = new GetActiveMissions(PageRequests.From(page, pageSize));
        var missions = await bus.InvokeAsync<Page<MissionView>>(query, cancellationToken).ConfigureAwait(false);
        return Results.Ok(missions);
    }
}

/// <summary>The body of <c>POST /api/operations/missions</c>. Carries no actor: identity comes from the token.</summary>
/// <param name="Origin">Where the mission starts.</param>
/// <param name="Destination">Where the mission ends; it may equal <paramref name="Origin"/>.</param>
/// <param name="RequiredCapacityKg">The load the mission requires, in kilograms.</param>
public sealed record CreateMissionRequest(string? Origin, string? Destination, decimal RequiredCapacityKg);

/// <summary>The body of <c>POST /api/operations/missions/{missionId}/schedule</c>.</summary>
/// <param name="ScheduledAt">The time the mission is scheduled for. No rule says it lies in the future (O-2).</param>
public sealed record ScheduleMissionRequest(DateTimeOffset ScheduledAt);

/// <summary>The body of <c>POST /api/operations/missions/{missionId}/assign</c>.</summary>
/// <param name="VehicleId">The vehicle to commit.</param>
/// <param name="DriverId">The driver to commit.</param>
public sealed record AssignMissionRequest(Guid VehicleId, Guid DriverId);
