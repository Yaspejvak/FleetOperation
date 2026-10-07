using System.Globalization;
using Fserp.FleetOperations.Api.Hosting;
using Fserp.FleetOperations.Modules.Operations.Application.Queries;
using Fserp.FleetOperations.Modules.Operations.Application.Views;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using MPCore.Application.Querying;
using MPCore.Application.Results;
using Wolverine;
using OperationsDomain = Fserp.FleetOperations.Modules.Operations.Domain;
using Proto = Fserp.FleetOperations.Api.Grpc.Operations.V1;

namespace Fserp.FleetOperations.Api.Grpc.Services;

/// <summary>
/// The Operations read surface over gRPC (docs/architecture.md, "Transport parity"). A thin adapter of
/// exactly the shape of <see cref="VehicleService"/>: it builds the same <see cref="GetMission"/> and
/// <see cref="GetActiveMissions"/> query records the REST endpoints build, sends them through the bus and
/// maps the outcome onto the proto messages. No rule and no query logic lives here.
/// </summary>
/// <remarks>
/// <para>
/// A failure returned by a query reaches the caller through <see cref="ResultExtensions.ValueOrThrow{T}"/>:
/// the <see cref="ResultFailureException"/> carries the very same <see cref="FailureDescriptor"/> the REST
/// path renders as Problem Details, so <c>MISSION_NOT_FOUND</c> is one failure answered on two transports
/// rather than two codes that can drift.
/// </para>
/// <para>
/// <c>required_capacity_kg</c> is a decimal string in invariant culture, because protobuf has no decimal
/// type and a <c>double</c> would change the value. <c>scheduled_at</c> is a
/// <see cref="Timestamp"/> and is absent while the mission is Draft; the two assigned ids are
/// <c>optional</c> and absent until the mission is assigned.
/// </para>
/// </remarks>
/// <param name="bus">The message bus the queries are sent through.</param>
public sealed class MissionService(IMessageBus bus)
    : global::Fserp.FleetOperations.Api.Grpc.Operations.V1.MissionService.MissionServiceBase
{
    /// <summary>
    /// One mission by id. An unknown id answers <c>NOT_FOUND</c> under <c>operations/MISSION_NOT_FOUND</c>.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="context">The server call context.</param>
    /// <remarks>
    /// A <c>mission_id</c> that is not a UUID parses to <see cref="Guid.Empty"/>, which
    /// <c>GetMissionValidator</c>'s <c>NotEmpty()</c> refuses, so it answers as a validation failure (L-7).
    /// No code is fabricated here for an unparsable id.
    /// </remarks>
    public override async Task<Proto.GetMissionResponse> GetMission(
        Proto.GetMissionRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        _ = Guid.TryParse(request.MissionId, out var missionId);
        var result = await bus
            .InvokeAsync<Result<MissionView>>(new GetMission(missionId), context.CancellationToken)
            .ConfigureAwait(false);

        return new Proto.GetMissionResponse { Mission = ToMessage(result.ValueOrThrow()) };
    }

    /// <summary>One bounded page of the active missions, through the same query the REST endpoint sends.</summary>
    /// <param name="request">The request; zero or absent values mean the first page at the default size.</param>
    /// <param name="context">The server call context.</param>
    public override async Task<Proto.GetActiveMissionsResponse> GetActiveMissions(
        Proto.GetActiveMissionsRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        // The same normalisation the REST endpoint applies, from the same function, so an absent page
        // means the same thing on both transports.
        var page = PageRequests.From(request.Page, request.PageSize);
        var missions = await bus
            .InvokeAsync<Page<MissionView>>(new GetActiveMissions(page), context.CancellationToken)
            .ConfigureAwait(false);

        var response = new Proto.GetActiveMissionsResponse
        {
            Page = missions.Number,
            PageSize = missions.Size,
            Total = missions.Total,
        };
        response.Missions.AddRange(missions.Items.Select(ToMessage));
        return response;
    }

    private static Proto.Mission ToMessage(MissionView view)
    {
        var message = new Proto.Mission
        {
            MissionId = view.Id.ToString(),
            Origin = view.Origin,
            Destination = view.Destination,
            RequiredCapacityKg = view.RequiredCapacityKg.ToString(CultureInfo.InvariantCulture),
            Status = view.Status switch
            {
                OperationsDomain.MissionStatus.Draft => Proto.MissionStatus.Draft,
                OperationsDomain.MissionStatus.Scheduled => Proto.MissionStatus.Scheduled,
                OperationsDomain.MissionStatus.Assigned => Proto.MissionStatus.Assigned,
                OperationsDomain.MissionStatus.InProgress => Proto.MissionStatus.InProgress,
                OperationsDomain.MissionStatus.Completed => Proto.MissionStatus.Completed,
                OperationsDomain.MissionStatus.Cancelled => Proto.MissionStatus.Cancelled,
                _ => Proto.MissionStatus.Unspecified,
            },
        };

        // scheduled_at is absent while the mission is Draft, not an epoch zero.
        if (view.ScheduledAt is { } scheduledAt)
        {
            message.ScheduledAt = Timestamp.FromDateTimeOffset(scheduledAt);
        }

        // The two assigned ids are optional: absent, not empty text, until the mission is assigned.
        if (view.AssignedVehicleId is { } vehicleId)
        {
            message.AssignedVehicleId = vehicleId.ToString();
        }

        if (view.AssignedDriverId is { } driverId)
        {
            message.AssignedDriverId = driverId.ToString();
        }

        return message;
    }
}
