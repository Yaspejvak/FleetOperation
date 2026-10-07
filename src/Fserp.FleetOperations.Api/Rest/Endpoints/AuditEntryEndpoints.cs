using Fserp.FleetOperations.Api.Hosting;
using Fserp.FleetOperations.Api.Security;
using Fserp.FleetOperations.Modules.Administration.Application.Queries;
using Fserp.FleetOperations.Modules.Administration.Application.Views;
using MPCore.Application.Querying;
using Wolverine;

namespace Fserp.FleetOperations.Api.Rest.Endpoints;

/// <summary>
/// The Administration surface over REST, under the module's prefix <c>/api/administration</c>: one read of
/// the business audit trail, behind the <c>Administrator</c> policy.
/// </summary>
/// <remarks>
/// REST is the whole contract for this module. docs/plans/administration.md records "gRPC: none" (AD-3),
/// so no proto and no gRPC service exists for the audit trail. The endpoint builds the query and sends it
/// through the bus; it decides nothing.
/// </remarks>
public static class AuditEntryEndpoints
{
    /// <summary>The route name of <c>GET /api/administration/audit-entries</c>.</summary>
    public const string GetAuditEntriesRoute = "GetAuditEntries";

    /// <summary>Maps the audit endpoints.</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The group, so the host can bind it to the REST listener.</returns>
    public static RouteGroupBuilder MapAuditEntryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // The module's one route prefix; every Administration route hangs below it.
        var group = endpoints.MapGroup("/api/administration").WithTags("Administration");

        group.MapGet("/audit-entries", GetAsync)
            .WithName(GetAuditEntriesRoute)
            // AD-1: Administrator is not a superset role and is not an OperationalReader. An operator or a
            // fleet manager is refused here, and an administrator is refused on every OperationalReader
            // endpoint.
            .RequireAuthorization(FleetOperationsPolicies.Administrator);

        return group;
    }

    /// <summary>
    /// One page of the trail, newest first. Every filter is optional; omitting all of them reads the whole
    /// trail, a page at a time.
    /// </summary>
    /// <param name="module">Match on module, for example <c>fleet</c>.</param>
    /// <param name="entityType">Match on the affected entity's type name, for example <c>Vehicle</c>.</param>
    /// <param name="entityId">Match on the affected entity's identifier.</param>
    /// <param name="actor">Match on the recorded actor's subject id.</param>
    /// <param name="correlationId">Match on the trace identifier of the operation.</param>
    /// <param name="category"><c>EntityChange</c> or <c>BusinessAction</c>.</param>
    /// <param name="outcome"><c>Succeeded</c>, <c>Rejected</c> or <c>Failed</c>.</param>
    /// <param name="from">Inclusive lower bound on the occurrence time, UTC.</param>
    /// <param name="to">
    /// <b>Exclusive</b> upper bound on the occurrence time, UTC (L-30). An entry stamped exactly
    /// <paramref name="to"/> is not returned; <c>[a, b)</c> and <c>[b, c)</c> tile the timeline without
    /// overlapping. Reading it as inclusive is the likelier mistake.
    /// </param>
    /// <param name="page">One-based page number; absent or zero takes the first page.</param>
    /// <param name="pageSize">Rows per page; absent or zero takes the default, and the maximum is capped.</param>
    /// <param name="bus">The message bus.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    private static async Task<IResult> GetAsync(
        string? module,
        string? entityType,
        string? entityId,
        string? actor,
        string? correlationId,
        AuditEntryCategory? category,
        AuditEntryOutcome? outcome,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int? page,
        int? pageSize,
        IMessageBus bus,
        CancellationToken cancellationToken)
    {
        // PageRequests.From decides what "not chosen" means, the same way as every other paged read here,
        // and PageRequest bounds the rest. The endpoint decides neither.
        var query = new GetAuditEntries(
            module,
            entityType,
            entityId,
            actor,
            correlationId,
            category,
            outcome,
            from,
            to,
            PageRequests.From(page, pageSize));

        var entries = await bus
            .InvokeAsync<Page<AuditEntryView>>(query, cancellationToken)
            .ConfigureAwait(false);
        return Results.Ok(entries);
    }
}
