using Fserp.FleetOperations.Modules.Administration.Application.Queries;
using Fserp.FleetOperations.Modules.Administration.Application.Views;
using MPCore.Application.Querying;
using MPCore.Audit;

namespace Fserp.FleetOperations.UnitTests.Administration;

/// <summary>
/// A stand-in for MP Core's audit read port. It records what the handler asked for and answers with what
/// the test wants the provider to have served, so both halves of the handler — the filter it builds and
/// the page it maps back — can be measured without a database.
/// </summary>
internal sealed class FakeAuditQuery : IAuditQuery
{
    private readonly AuditPage _page;

    public FakeAuditQuery(AuditPage page) => _page = page;

    public FakeAuditQuery(params AuditEntry[] entries)
        : this(new AuditPage(entries, 1, entries.Length == 0 ? 20 : entries.Length, entries.Length))
    {
    }

    public AuditQueryFilter? Filter { get; private set; }

    public AuditPageRequest? Page { get; private set; }

    public int Calls { get; private set; }

    public Task<AuditPage> QueryAsync(
        AuditQueryFilter filter,
        AuditPageRequest page,
        CancellationToken cancellationToken)
    {
        Filter = filter;
        Page = page;
        Calls++;
        return Task.FromResult(_page);
    }
}

/// <summary>Entries shaped like the ones the other modules write, for the mapping tests.</summary>
internal static class AuditEntries
{
    public static AuditEntry Succeeded(string module = "fleet", string action = "VehicleRegistered") => new()
    {
        OccurredAtUtc = new DateTimeOffset(2026, 10, 5, 9, 30, 0, TimeSpan.Zero),
        Actor = new AuditActor(AuditActorKind.User, "subject-7", "fleet-ops-ui", "ada"),
        Module = module,
        Category = AuditCategory.BusinessAction,
        EntityType = "Vehicle",
        EntityId = "01920000-0000-7000-8000-000000000001",
        Action = action,
        Outcome = AuditOutcome.Succeeded,
        CorrelationId = "0af7651916cd43dd8448eb211c80319c",
        OperationId = "b7ad6b7169203331",
        Reason = null,
        Changes = [],
        Metadata = new Dictionary<string, string>(),
    };

    public static AuditEntry Rejected() => new()
    {
        OccurredAtUtc = new DateTimeOffset(2026, 10, 5, 9, 31, 0, TimeSpan.Zero),
        Actor = AuditActor.Anonymous,
        Module = "operations",
        Category = AuditCategory.BusinessAction,
        EntityType = "Mission",
        EntityId = "01920000-0000-7000-8000-0000000000aa",
        Action = "MissionAssigned",
        Outcome = AuditOutcome.Rejected,
        Failure = new AuditFailure("fleet", "VEHICLE_UNDER_MAINTENANCE"),
        Reason = "The vehicle is under maintenance.",
        CorrelationId = "cafe",
        Changes = [],
        Metadata = new Dictionary<string, string>(),
    };

    public static AuditEntry BySystemJob() => new()
    {
        OccurredAtUtc = new DateTimeOffset(2026, 10, 5, 2, 0, 0, TimeSpan.Zero),
        Actor = AuditActor.SystemActor("nightly-reconciliation"),
        Module = "fleet",
        Category = AuditCategory.EntityChange,
        EntityType = "Vehicle",
        EntityId = "01920000-0000-7000-8000-000000000002",
        Action = "Updated",
        Outcome = AuditOutcome.Succeeded,
        Changes = [],
        Metadata = new Dictionary<string, string>(),
    };
}

/// <summary>
/// <c>GetAuditEntriesHandler</c> against a fake port: every query parameter reaches the one filter member
/// the plan maps it onto, and every entry comes back as this module's own view.
/// </summary>
public sealed class GetAuditEntriesHandlerTests
{
    private static GetAuditEntries Query(
        string? module = null,
        string? entityType = null,
        string? entityId = null,
        string? actor = null,
        string? correlationId = null,
        AuditEntryCategory? category = null,
        AuditEntryOutcome? outcome = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        PageRequest? page = null) =>
        new(module, entityType, entityId, actor, correlationId, category, outcome, from, to, page ?? new PageRequest(1, 20));

    [Fact]
    public async Task Every_parameter_reaches_the_one_filter_member_the_plan_maps_it_onto()
    {
        var from = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);
        var port = new FakeAuditQuery();

        await GetAuditEntriesHandler.Handle(
            Query("fleet", "Vehicle", "v-1", "subject-7", "trace-9", AuditEntryCategory.BusinessAction, AuditEntryOutcome.Rejected, from, to),
            port,
            CancellationToken.None);

        var filter = Assert.IsType<AuditQueryFilter>(port.Filter);
        Assert.Equal("fleet", filter.Module);
        Assert.Equal("Vehicle", filter.EntityType);
        Assert.Equal("v-1", filter.EntityId);
        // The plan's "actor (subject id)".
        Assert.Equal("subject-7", filter.ActorSubjectId);
        Assert.Equal("trace-9", filter.CorrelationId);
        Assert.Equal(AuditCategory.BusinessAction, filter.Category);
        Assert.Equal(AuditOutcome.Rejected, filter.Outcome);
        Assert.Equal(from, filter.FromUtc);
        // L-30: "to" is the exclusive upper bound, and MP Core's ToUtc is documented as exclusive too, so
        // the value travels unchanged rather than being nudged by a tick at either end.
        Assert.Equal(to, filter.ToUtc);
    }

    [Fact]
    public async Task An_empty_query_filters_on_nothing()
    {
        var port = new FakeAuditQuery();

        await GetAuditEntriesHandler.Handle(Query(), port, CancellationToken.None);

        Assert.Equal(new AuditQueryFilter(), port.Filter);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_present_but_blank_parameter_is_no_filter_rather_than_a_filter_on_nothing(string blank)
    {
        // ?module= is the caller saying nothing, not asking for rows whose module is the empty string --
        // which would match none and look like an empty trail.
        var port = new FakeAuditQuery();

        await GetAuditEntriesHandler.Handle(
            Query(module: blank, entityType: blank, entityId: blank, actor: blank, correlationId: blank),
            port,
            CancellationToken.None);

        Assert.Null(port.Filter!.Module);
        Assert.Null(port.Filter.EntityType);
        Assert.Null(port.Filter.EntityId);
        Assert.Null(port.Filter.ActorSubjectId);
        Assert.Null(port.Filter.CorrelationId);
    }

    [Fact]
    public async Task Surrounding_whitespace_is_trimmed_off_a_filter_value()
    {
        var port = new FakeAuditQuery();

        await GetAuditEntriesHandler.Handle(Query(module: "  fleet  "), port, CancellationToken.None);

        Assert.Equal("fleet", port.Filter!.Module);
    }

    [Fact]
    public async Task The_page_travels_as_the_providers_own_page_request()
    {
        var port = new FakeAuditQuery();

        await GetAuditEntriesHandler.Handle(Query(page: new PageRequest(3, 50)), port, CancellationToken.None);

        Assert.Equal(new AuditPageRequest(3, 50), port.Page);
    }

    [Fact]
    public async Task The_page_size_a_caller_asks_for_is_bounded_before_it_reaches_the_port()
    {
        // The plan's "pageSize within bounds". PageRequest normalises itself, so neither a zero nor a
        // million can reach the provider however the caller phrased it.
        var port = new FakeAuditQuery();

        await GetAuditEntriesHandler.Handle(Query(page: new PageRequest(0, 1_000_000)), port, CancellationToken.None);

        Assert.Equal(1, port.Page!.Page);
        Assert.Equal(PageRequest.MaximumSize, port.Page.Size);
    }

    [Fact]
    public async Task The_page_reported_back_is_the_one_the_provider_served_not_the_one_requested()
    {
        // MP Core's EntityFrameworkAuditQuery caps the size itself. Echoing the request would describe a
        // page that was never served, and a caller paging through the trail would skip rows.
        var port = new FakeAuditQuery(new AuditPage([AuditEntries.Succeeded()], 2, 25, 412));

        var page = await GetAuditEntriesHandler.Handle(Query(page: new PageRequest(2, 200)), port, CancellationToken.None);

        Assert.Equal(2, page.Number);
        Assert.Equal(25, page.Size);
        Assert.Equal(412, page.Total);
        Assert.Single(page.Items);
    }

    [Fact]
    public async Task An_empty_trail_is_an_empty_page_and_not_a_failure()
    {
        var page = await GetAuditEntriesHandler.Handle(Query(), new FakeAuditQuery(), CancellationToken.None);

        Assert.Empty(page.Items);
        Assert.Equal(0, page.Total);
    }

    [Fact]
    public async Task A_succeeded_entry_answers_who_what_when_which_entity_and_the_result()
    {
        var page = await GetAuditEntriesHandler.Handle(
            Query(),
            new FakeAuditQuery(AuditEntries.Succeeded()),
            CancellationToken.None);

        var view = Assert.Single(page.Items);
        // when
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 9, 30, 0, TimeSpan.Zero), view.OccurredAtUtc);
        // who
        Assert.Equal(AuditEntryActorKind.User, view.Actor.Kind);
        Assert.Equal("subject-7", view.Actor.SubjectId);
        Assert.Equal("fleet-ops-ui", view.Actor.ClientId);
        Assert.Equal("ada", view.Actor.UserName);
        // what
        Assert.Equal("VehicleRegistered", view.Action);
        Assert.Equal(AuditEntryCategory.BusinessAction, view.Category);
        // which entity
        Assert.Equal("fleet", view.Module);
        Assert.Equal("Vehicle", view.EntityType);
        Assert.Equal("01920000-0000-7000-8000-000000000001", view.EntityId);
        // result
        Assert.Equal(AuditEntryOutcome.Succeeded, view.Outcome);
        Assert.Null(view.Failure);
        // and the thread back to the trace
        Assert.Equal("0af7651916cd43dd8448eb211c80319c", view.CorrelationId);
    }

    [Fact]
    public async Task A_rejected_entry_carries_the_failures_domain_and_code()
    {
        var page = await GetAuditEntriesHandler.Handle(
            Query(),
            new FakeAuditQuery(AuditEntries.Rejected()),
            CancellationToken.None);

        var view = Assert.Single(page.Items);
        Assert.Equal(AuditEntryOutcome.Rejected, view.Outcome);
        Assert.Equal(new AuditEntryFailureView("fleet", "VEHICLE_UNDER_MAINTENANCE"), view.Failure);
        Assert.Equal(AuditEntryActorKind.Anonymous, view.Actor.Kind);
        Assert.Null(view.Actor.SubjectId);
    }

    [Fact]
    public async Task A_background_jobs_entry_still_answers_who()
    {
        // MP Core records a system actor's job name in UserName and leaves SubjectId null. Projecting the
        // subject alone would answer "who" with nothing for every background change, so the view carries
        // the whole recorded actor.
        var page = await GetAuditEntriesHandler.Handle(
            Query(),
            new FakeAuditQuery(AuditEntries.BySystemJob()),
            CancellationToken.None);

        var view = Assert.Single(page.Items);
        Assert.Equal(AuditEntryActorKind.System, view.Actor.Kind);
        Assert.Null(view.Actor.SubjectId);
        Assert.Equal("nightly-reconciliation", view.Actor.UserName);
        Assert.Equal(AuditEntryCategory.EntityChange, view.Category);
        Assert.Equal("Updated", view.Action);
    }

    [Fact]
    public async Task The_order_the_provider_served_is_preserved()
    {
        // "Newest first" is the provider's guarantee; the handler must not re-sort or reverse it.
        var newest = AuditEntries.Succeeded(action: "MissionCompleted");
        var older = AuditEntries.Succeeded(action: "MissionStarted");
        var port = new FakeAuditQuery(new AuditPage([newest, older], 1, 20, 2));

        var page = await GetAuditEntriesHandler.Handle(Query(), port, CancellationToken.None);

        Assert.Equal(["MissionCompleted", "MissionStarted"], page.Items.Select(item => item.Action));
    }

    [Fact]
    public async Task The_port_is_asked_exactly_once()
    {
        var port = new FakeAuditQuery(AuditEntries.Succeeded());

        await GetAuditEntriesHandler.Handle(Query(), port, CancellationToken.None);

        Assert.Equal(1, port.Calls);
    }

    [Fact]
    public async Task The_cancellation_token_reaches_the_port()
    {
        using var source = new CancellationTokenSource();
        await source.CancelAsync();
        var port = new ObservingAuditQuery();

        await GetAuditEntriesHandler.Handle(Query(), port, source.Token);

        Assert.True(port.Observed.IsCancellationRequested);
    }

    private sealed class ObservingAuditQuery : IAuditQuery
    {
        public CancellationToken Observed { get; private set; }

        public Task<AuditPage> QueryAsync(AuditQueryFilter filter, AuditPageRequest page, CancellationToken cancellationToken)
        {
            Observed = cancellationToken;
            return Task.FromResult(new AuditPage([], page.Page, page.Size, 0));
        }
    }
}
