using MPCore.Application.Results;
using MPCore.Audit;
using MPCore.Domain.Rules;

namespace Fserp.FleetOperations.Modules.Operations.Application;

/// <summary>
/// Writes the module's rejected attempts. One place, because every Operations command records a refusal
/// the same way: detached from the business transaction, so the record survives the rollback the refusal
/// causes (docs/architecture.md, "Business audit path").
/// </summary>
/// <remarks>
/// It is a helper over <see cref="IBusinessAuditRecorder"/>, not a port: handlers still declare the
/// recorder and pass it in, so nothing here is resolved behind a handler's back.
/// </remarks>
public static class MissionAuditing
{
    /// <summary>Records a refusal caused by a broken business rule, under the rule's own domain and code.</summary>
    /// <param name="audit">The business audit recorder the handler declared.</param>
    /// <param name="action">The audited action the attempt belongs to.</param>
    /// <param name="rejected">The broken rule.</param>
    /// <param name="missionId">The mission the attempt was made on.</param>
    /// <param name="metadata">What the attempt asked for.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public static ValueTask RecordRejectedAsync(
        IBusinessAuditRecorder audit,
        string action,
        BusinessRuleValidationException rejected,
        Guid missionId,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(rejected);
        return RecordRejectedAsync(
            audit,
            action,
            new AuditFailure(rejected.Rule.ErrorDomain, rejected.Rule.Code),
            missionId,
            metadata,
            cancellationToken);
    }

    /// <summary>
    /// Records a refusal expressed as a failure descriptor: a not-found answer this handler returns, or a
    /// <see cref="ResultFailureException"/> another module threw.
    /// </summary>
    /// <param name="audit">The business audit recorder the handler declared.</param>
    /// <param name="action">The audited action the attempt belongs to.</param>
    /// <param name="failure">The failure the caller receives.</param>
    /// <param name="missionId">The mission the attempt was made on.</param>
    /// <param name="metadata">What the attempt asked for.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public static ValueTask RecordRejectedAsync(
        IBusinessAuditRecorder audit,
        string action,
        FailureDescriptor failure,
        Guid missionId,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(failure);
        return RecordRejectedAsync(
            audit,
            action,
            new AuditFailure(failure.Identity.Domain, failure.Identity.Code),
            missionId,
            metadata,
            cancellationToken);
    }

    private static ValueTask RecordRejectedAsync(
        IBusinessAuditRecorder audit,
        string action,
        AuditFailure failure,
        Guid missionId,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken) =>
        audit.RecordAttemptAsync(
            OperationsAudit.Module,
            action,
            AuditOutcome.Rejected,
            failure,
            null,
            OperationsAudit.MissionEntity,
            missionId.ToString(),
            metadata,
            cancellationToken);
}
