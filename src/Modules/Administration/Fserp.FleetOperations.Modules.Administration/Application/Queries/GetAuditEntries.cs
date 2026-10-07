using Fserp.FleetOperations.Modules.Administration.Application.Views;
using MPCore.Application.Messaging;
using MPCore.Application.Querying;
using MPCore.Audit;

namespace Fserp.FleetOperations.Modules.Administration.Application.Queries;

/// <summary>
/// Reads one bounded page of the business audit trail, newest first. Every member is optional except the
/// page, and each one maps onto exactly one member of MP Core's <c>AuditQueryFilter</c>
/// (docs/plans/administration.md).
/// </summary>
/// <remarks>
/// <para>
/// The trail is never cached: an administrator investigating an incident must see it as it is now
/// (the plan's "Cache candidates: none").
/// </para>
/// <para>
/// <see cref="ActorSubjectId"/> is a <b>filter</b> over the actors already recorded in the trail, not the
/// caller's identity. The caller's identity comes from the validated token through the policy on the
/// endpoint and is never read from this message; a query that named its own actor would be exactly the
/// mistake CLAUDE.md forbids.
/// </para>
/// </remarks>
/// <param name="Module">Match on module, for example <c>fleet</c>. Null or blank means every module.</param>
/// <param name="EntityType">Match on the affected entity's type name. Null or blank means every type.</param>
/// <param name="EntityId">Match on the affected entity's identifier. Null or blank means every entity.</param>
/// <param name="ActorSubjectId">Match on the recorded actor's subject (the <c>actor</c> query parameter).</param>
/// <param name="CorrelationId">Match on the trace identifier, to pull one operation out of the trail.</param>
/// <param name="Category">Match on <c>EntityChange</c> or <c>BusinessAction</c>. Null means both.</param>
/// <param name="Outcome">Match on <c>Succeeded</c>, <c>Rejected</c> or <c>Failed</c>. Null means all three.</param>
/// <param name="From">
/// Inclusive lower bound on the occurrence time, UTC. An entry stamped exactly <c>from</c> is included.
/// </param>
/// <param name="To">
/// <b>Exclusive</b> upper bound on the occurrence time, UTC (L-30, and the plan). An entry stamped exactly
/// <c>to</c> is <b>not</b> returned, so consecutive windows <c>[a, b)</c> and <c>[b, c)</c> neither overlap
/// nor lose a row. Reading it as inclusive is the likelier mistake, which is why it is said here and in the
/// README.
/// </param>
/// <param name="Page">The page requested, already normalised by <see cref="PageRequest"/>.</param>
public sealed record GetAuditEntries(
    string? Module,
    string? EntityType,
    string? EntityId,
    string? ActorSubjectId,
    string? CorrelationId,
    AuditEntryCategory? Category,
    AuditEntryOutcome? Outcome,
    DateTimeOffset? From,
    DateTimeOffset? To,
    PageRequest Page) : IQuery<Page<AuditEntryView>>;

/// <summary>
/// Handles <see cref="GetAuditEntries"/>. Reads only, through the one port MP Core owns for the trail.
/// </summary>
/// <remarks>
/// <para>
/// Lead decision L-29: the handler declares <see cref="IAuditQuery"/> and its
/// <see cref="CancellationToken"/> and nothing else — no <c>IUnitOfWork</c>, no repository, no read model
/// of this module's own, and no cache port.
/// </para>
/// <para>
/// It does <b>not</b> take <c>IBusinessAuditRecorder</c>, and must not: reading the trail is not itself an
/// audited business action (AD-2). A read that recorded itself would grow the trail every time someone
/// looked at it.
/// </para>
/// </remarks>
public static class GetAuditEntriesHandler
{
    /// <summary>
    /// Returns the page the provider served. An empty page is a page, never a <c>404</c>: a window with no
    /// recorded action is a fact about the window.
    /// </summary>
    /// <param name="query">The query.</param>
    /// <param name="trail">MP Core's read port over <c>audit.entries</c>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<Page<AuditEntryView>> Handle(
        GetAuditEntries query,
        IAuditQuery trail,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(trail);

        var filter = new AuditQueryFilter
        {
            Module = Filter(query.Module),
            EntityType = Filter(query.EntityType),
            EntityId = Filter(query.EntityId),
            ActorSubjectId = Filter(query.ActorSubjectId),
            CorrelationId = Filter(query.CorrelationId),
            Category = CategoryOf(query.Category),
            Outcome = OutcomeOf(query.Outcome),
            FromUtc = query.From,
            ToUtc = query.To,
        };

        var page = await trail
            .QueryAsync(filter, new AuditPageRequest(query.Page.Number, query.Page.Size), cancellationToken)
            .ConfigureAwait(false);

        // The page number and size reported are the ones the provider applied, not the ones asked for:
        // MP Core caps the size (EntityFrameworkAuditQuery.MaxPageSize), so echoing the request would
        // describe a page that was never served.
        return new Page<AuditEntryView>(
            page.Items.Select(AuditEntryView.From).ToList(),
            page.Page,
            page.Size,
            page.Total);
    }

    /// <summary>
    /// A present-but-empty query parameter (<c>?module=</c>) is "no filter", not "a module whose name is
    /// the empty string" — the latter matches nothing and would silently return an empty page.
    /// </summary>
    private static string? Filter(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static AuditCategory? CategoryOf(AuditEntryCategory? category) => category switch
    {
        null => null,
        AuditEntryCategory.EntityChange => AuditCategory.EntityChange,
        AuditEntryCategory.BusinessAction => AuditCategory.BusinessAction,
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown audit category."),
    };

    private static AuditOutcome? OutcomeOf(AuditEntryOutcome? outcome) => outcome switch
    {
        null => null,
        AuditEntryOutcome.Succeeded => AuditOutcome.Succeeded,
        AuditEntryOutcome.Rejected => AuditOutcome.Rejected,
        AuditEntryOutcome.Failed => AuditOutcome.Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown audit outcome."),
    };
}
