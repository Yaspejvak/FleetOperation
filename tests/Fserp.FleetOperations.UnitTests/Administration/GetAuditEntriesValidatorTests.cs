using Fserp.FleetOperations.Modules.Administration.Application.Queries;
using Fserp.FleetOperations.Modules.Administration.Application.Validators;
using Fserp.FleetOperations.Modules.Administration.Application.Views;
using MPCore.Application.Querying;
using MPCore.Validation.FluentValidation;

namespace Fserp.FleetOperations.UnitTests.Administration;

/// <summary>
/// The three checks docs/plans/administration.md names, and nothing else: <c>from &lt; to</c> when both are
/// given, each enum against its allowlist, and the page size within bounds.
/// </summary>
public sealed class GetAuditEntriesValidatorTests
{
    private readonly GetAuditEntriesValidator _validator = new();

    private static readonly DateTimeOffset Noon = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private static GetAuditEntries Query(
        AuditEntryCategory? category = null,
        AuditEntryOutcome? outcome = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        PageRequest? page = null) =>
        new(null, null, null, null, null, category, outcome, from, to, page ?? new PageRequest(1, 20));

    [Fact]
    public void A_query_with_no_parameter_at_all_passes()
    {
        // Every filter is optional: reading the whole trail a page at a time is the default.
        Assert.True(_validator.Validate(Query()).IsValid);
    }

    [Fact]
    public void A_window_whose_start_is_before_its_end_passes()
    {
        Assert.True(_validator.Validate(Query(from: Noon, to: Noon.AddHours(1))).IsValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_window_that_does_not_move_forward_is_a_field_violation(int hours)
    {
        // "to" is exclusive (L-30), so from == to selects nothing: it is a mistake worth telling the
        // caller about rather than an empty page they will read as "nothing happened".
        var result = _validator.Validate(Query(from: Noon, to: Noon.AddHours(hours)));

        var violation = ValidationFailures.ToViolation(Assert.Single(result.Errors));
        Assert.Equal("from", violation.FieldPath);
        Assert.Equal("AUDIT_RANGE_INVALID", violation.RuleCode);
        Assert.Equal("administration.audit_range_invalid", violation.Message.Key);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void One_bound_on_its_own_is_an_open_ended_window_and_passes(bool hasFrom, bool hasTo)
    {
        // The plan says "from < to when both given". One bound alone is legitimate.
        Assert.True(_validator.Validate(Query(from: hasFrom ? Noon : null, to: hasTo ? Noon : null)).IsValid);
    }

    [Theory]
    [InlineData(AuditEntryCategory.EntityChange)]
    [InlineData(AuditEntryCategory.BusinessAction)]
    public void Each_category_the_plan_names_passes(AuditEntryCategory category)
    {
        Assert.True(_validator.Validate(Query(category: category)).IsValid);
    }

    [Theory]
    [InlineData(AuditEntryOutcome.Succeeded)]
    [InlineData(AuditEntryOutcome.Rejected)]
    [InlineData(AuditEntryOutcome.Failed)]
    public void Each_outcome_the_plan_names_passes(AuditEntryOutcome outcome)
    {
        Assert.True(_validator.Validate(Query(outcome: outcome)).IsValid);
    }

    [Fact]
    public void A_category_outside_the_allowlist_is_refused()
    {
        var result = _validator.Validate(Query(category: (AuditEntryCategory)42));

        Assert.False(result.IsValid);
        Assert.Equal("category", ValidationFailures.ToViolation(Assert.Single(result.Errors)).FieldPath);
    }

    [Fact]
    public void An_outcome_outside_the_allowlist_is_refused()
    {
        var result = _validator.Validate(Query(outcome: (AuditEntryOutcome)42));

        Assert.False(result.IsValid);
        Assert.Equal("outcome", ValidationFailures.ToViolation(Assert.Single(result.Errors)).FieldPath);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5, -5)]
    [InlineData(1, 1_000_000)]
    public void The_page_is_already_within_bounds_whatever_the_caller_asked_for(int number, int size)
    {
        // The plan's third check. It is not a validator rule here and must not be: PageRequest normalises
        // itself on construction, so no caller can build a GetAuditEntries that is out of bounds. The
        // bound is proved where it is enforced instead of being restated as a rule that cannot fail.
        var query = Query(page: new PageRequest(number, size));

        Assert.True(query.Page.Number >= 1);
        Assert.InRange(query.Page.Size, 1, PageRequest.MaximumSize);
        Assert.True(_validator.Validate(query).IsValid);
    }

    [Fact]
    public void The_validator_declares_only_the_rules_the_plan_names()
    {
        // A census, so a fourth rule cannot be added without this list changing. One descriptor per
        // RuleFor: From (the range), Category and Outcome (the allowlists).
        var fields = _validator.CreateDescriptor().Rules
            .Select(rule => rule.PropertyName)
            .Order()
            .ToList();

        Assert.Equal(["Category", "From", "Outcome"], fields);
    }
}
