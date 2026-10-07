using MPCore.Audit;

namespace Fserp.FleetOperations.Modules.Administration.Application.Views;

/// <summary>
/// One entry of the business audit trail as an administrator sees it, owned by this module.
/// </summary>
/// <remarks>
/// <para>
/// MP Core's <c>AuditEntry</c> is never returned to a caller. This record is the REST contract, and the
/// mapping in <see cref="From(AuditEntry)"/> is the one place that knows both shapes, so the contract does
/// not move if the package type does (docs/plans/administration.md).
/// </para>
/// <para>
/// It answers the five questions of challenge section 10 and nothing more: <b>who</b>
/// (<see cref="Actor"/>), <b>what</b> (<see cref="Action"/>, <see cref="Category"/>), <b>when</b>
/// (<see cref="OccurredAtUtc"/>), <b>which entity</b> (<see cref="Module"/>, <see cref="EntityType"/>,
/// <see cref="EntityId"/>), <b>result</b> (<see cref="Outcome"/>, <see cref="Failure"/>), plus
/// <see cref="CorrelationId"/> to join a row with a trace. The stored entry also carries
/// <c>TenantId</c>, <c>OperationId</c>, <c>Reason</c>, <c>Changes</c> and <c>Metadata</c>; the plan's
/// field list does not name them, so they are deliberately not projected here. Adding one is a contract
/// change for the owner to approve, not a detail to slip in.
/// </para>
/// </remarks>
/// <param name="OccurredAtUtc">When the action happened, UTC.</param>
/// <param name="Actor">Who acted, as the validated token established it — never a header or a body.</param>
/// <param name="Module">The bounded context the action belongs to.</param>
/// <param name="Category">Entity change or business action.</param>
/// <param name="EntityType">The affected entity's type name, when there is one.</param>
/// <param name="EntityId">The affected entity's identifier, when there is one.</param>
/// <param name="Action"><c>Created</c>, <c>Updated</c>, <c>Deleted</c>, or the business action's own name.</param>
/// <param name="Outcome">Whether the action succeeded, was rejected or failed.</param>
/// <param name="Failure">The failure's domain and code, for a rejected or failed outcome; otherwise null.</param>
/// <param name="CorrelationId">The trace identifier of the operation, to join the row with its trace.</param>
public sealed record AuditEntryView(
    DateTimeOffset OccurredAtUtc,
    AuditEntryActorView Actor,
    string? Module,
    AuditEntryCategory Category,
    string? EntityType,
    string? EntityId,
    string? Action,
    AuditEntryOutcome Outcome,
    AuditEntryFailureView? Failure,
    string? CorrelationId)
{
    /// <summary>Projects one stored entry into the module's own view.</summary>
    /// <param name="entry">The entry as MP Core's query port returned it.</param>
    public static AuditEntryView From(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new AuditEntryView(
            entry.OccurredAtUtc,
            AuditEntryActorView.From(entry.Actor),
            entry.Module,
            CategoryOf(entry.Category),
            entry.EntityType,
            entry.EntityId,
            entry.Action,
            OutcomeOf(entry.Outcome),
            entry.Failure is null ? null : new AuditEntryFailureView(entry.Failure.Domain, entry.Failure.Code),
            entry.CorrelationId);
    }

    // Mapped by member, never by a cast: the two enums agree on names, and a cast would silently survive
    // a renumbering in the package while producing the wrong word in the contract.
    private static AuditEntryCategory CategoryOf(AuditCategory category) => category switch
    {
        AuditCategory.EntityChange => AuditEntryCategory.EntityChange,
        AuditCategory.BusinessAction => AuditEntryCategory.BusinessAction,
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown audit category."),
    };

    private static AuditEntryOutcome OutcomeOf(AuditOutcome outcome) => outcome switch
    {
        AuditOutcome.Succeeded => AuditEntryOutcome.Succeeded,
        AuditOutcome.Rejected => AuditEntryOutcome.Rejected,
        AuditOutcome.Failed => AuditEntryOutcome.Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown audit outcome."),
    };
}

/// <summary>
/// Who performed an audited action. The whole recorded snapshot, because a single field cannot answer
/// "who" for every row: MP Core records a background job's name in <c>UserName</c> with no subject, and an
/// anonymous attempt has none of the three. The <c>actor</c> query parameter still filters on
/// <see cref="SubjectId"/> alone, as the plan specifies.
/// </summary>
/// <param name="Kind">User, service, system or anonymous.</param>
/// <param name="SubjectId">The token's subject, when there was one. The value the <c>actor</c> filter matches.</param>
/// <param name="ClientId">The client the token was issued to, when recorded.</param>
/// <param name="UserName">The display name, or the job name for a system actor.</param>
public sealed record AuditEntryActorView(
    AuditEntryActorKind Kind,
    string? SubjectId,
    string? ClientId,
    string? UserName)
{
    /// <summary>Projects the recorded actor into the module's own view.</summary>
    /// <param name="actor">The actor as MP Core recorded it.</param>
    public static AuditEntryActorView From(AuditActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return new AuditEntryActorView(KindOf(actor.Kind), actor.SubjectId, actor.ClientId, actor.UserName);
    }

    private static AuditEntryActorKind KindOf(AuditActorKind kind) => kind switch
    {
        AuditActorKind.Anonymous => AuditEntryActorKind.Anonymous,
        AuditActorKind.User => AuditEntryActorKind.User,
        AuditActorKind.Service => AuditEntryActorKind.Service,
        AuditActorKind.System => AuditEntryActorKind.System,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown audit actor kind."),
    };
}

/// <summary>
/// The machine-readable identity of the failure a rejected or failed attempt carries, as the module's own
/// type. Mirrors the error domain and code the caller of the original action saw.
/// </summary>
/// <param name="Domain">The error domain, for example <c>fleet</c>.</param>
/// <param name="Code">The UPPER_SNAKE code, for example <c>VEHICLE_UNDER_MAINTENANCE</c>.</param>
public sealed record AuditEntryFailureView(string Domain, string Code);
