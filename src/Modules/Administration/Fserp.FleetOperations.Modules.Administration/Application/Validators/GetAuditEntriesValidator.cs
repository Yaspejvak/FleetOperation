using FluentValidation;
using Fserp.FleetOperations.Modules.Administration.Application.Queries;

namespace Fserp.FleetOperations.Modules.Administration.Application.Validators;

/// <summary>
/// Input shape of <see cref="GetAuditEntries"/>, checked before the handler runs (400). The plan names
/// exactly three checks and this declares exactly those: <c>from &lt; to</c> when both are given, each enum
/// against its allowlist, and the page size within bounds.
/// </summary>
/// <remarks>
/// <para>
/// There is no rule here for the page, and that is not an omission. <c>pageSize</c> is held within bounds
/// by MP Core's <c>PageRequest</c>, which normalises itself on construction (default 20, maximum 200), and
/// again by the audit provider's own cap. A FluentValidation rule over
/// <c>GetAuditEntries.Page.Size</c> could therefore never fail for any input any caller can produce — it
/// would be a rule that only looks like protection. The bound is proved where it is actually enforced, in
/// <c>GetAuditEntriesValidatorTests</c> and the host tests.
/// </para>
/// <para>
/// Nothing else is checked. The plan lists no other validation, and a filter that matches no row is an
/// empty page, not a bad request.
/// </para>
/// </remarks>
public sealed class GetAuditEntriesValidator : AbstractValidator<GetAuditEntries>
{
    /// <summary>Declares the rules.</summary>
    public GetAuditEntriesValidator()
    {
        // from < to, strictly, and only when the caller gave both. One bound alone is an open-ended
        // window, which is legitimate. "to" is exclusive (L-30), so from == to selects nothing and is
        // refused rather than silently answered with an empty page.
        RuleFor(query => query.From)
            .Must(static (query, from) => from is null || query.To is null || from < query.To)
            .WithErrorCode(AdministrationErrors.AuditRangeInvalid)
            .WithMessage(AdministrationErrors.MessageKey(AdministrationErrors.AuditRangeInvalid));

        // The allowlist of each enum. A value outside it reaches the caller as a field violation rather
        // than as a filter nobody declared.
        RuleFor(query => query.Category).IsInEnum();
        RuleFor(query => query.Outcome).IsInEnum();
    }
}
