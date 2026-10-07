using Fserp.FleetOperations.IntegrationTests.Operations;
using Fserp.FleetOperations.Modules.Administration.Application.Queries;
using Fserp.FleetOperations.Modules.Administration.Application.Views;
using Fserp.FleetOperations.Modules.Fleet.Application;
using Fserp.FleetOperations.Modules.Fleet.Contracts;
using Fserp.FleetOperations.Modules.Operations.Application;
using Microsoft.Extensions.DependencyInjection;
using MPCore.Application.Querying;
using MPCore.Audit;

namespace Fserp.FleetOperations.IntegrationTests.Administration;

/// <summary>
/// The Administration read against a real PostgreSQL, through the real
/// <c>GetAuditEntriesHandler</c> and MP Core's real <c>IAuditQuery</c>.
/// </summary>
/// <remarks>
/// <para>
/// Everything this module promises is a promise about the provider, and the provider is the only thing
/// that can keep it: "newest first" is an ORDER BY nobody here writes, and "<c>to</c> is exclusive"
/// (L-30) is a comparison operator inside a package. Both are stated in the README, in the endpoint's XML
/// documentation and in the query's — and neither can be established by a fake, because a fake would
/// simply return whatever this test handed it. They are established here or nowhere.
/// </para>
/// <para>
/// <b>These have been run against a real PostgreSQL and they pass.</b> "Newest first", the inclusive
/// lower bound and the exclusive upper bound are this repository's <em>measured</em> behaviour, not a
/// quotation from MP Core's documentation. The measurement is of the database the run used — the
/// Testcontainers PostgreSQL the fixture starts, or whatever <c>FLEETOPS_TEST_POSTGRES</c> points at —
/// and it says nothing about any other provider or any other version.
/// </para>
/// <para>
/// Still skipped with the fixture's reason when no PostgreSQL is reachable, so on a machine without one
/// these report Skipped rather than passing vacuously. A skipped run re-opens the question; it does not
/// retract the measurement.
/// </para>
/// </remarks>
[Collection(PostgreSqlCollection.Name)]
public sealed class AuditTrailQueryTests(PostgreSqlFixture database)
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
        int number = 1,
        int size = 100) =>
        new(module, entityType, entityId, actor, correlationId, category, outcome, from, to, new PageRequest(number, size));

    private async Task<Page<AuditEntryView>> Read(GetAuditEntries query)
    {
        await using var scope = database.Scope();
        return await GetAuditEntriesHandler.Handle(
            query,
            scope.ServiceProvider.GetRequiredService<IAuditQuery>(),
            CancellationToken.None);
    }

    [SkippableFact]
    public async Task A_recorded_action_comes_back_through_the_module_view()
    {
        database.SkipWhenUnavailable();
        var vehicleId = await database.RegisterVehicle(capacityKg: 4200m, type: VehicleType.Truck);

        // FleetAudit.Module ("Fleet") rather than a literal: the filter and the assertion are then the
        // same constant the recorder writes, and neither can drift from the module's audit name.
        var page = await Read(Query(module: FleetAudit.Module, entityId: vehicleId.ToString()));

        Assert.NotEmpty(page.Items);
        Assert.All(page.Items, entry =>
        {
            Assert.Equal(FleetAudit.Module, entry.Module);
            Assert.Equal(vehicleId.ToString(), entry.EntityId);
        });
        // Who: the subject the fixture's actor accessor carries, as a validated token would.
        Assert.All(page.Items, entry => Assert.Equal(PostgreSqlFixture.ActorSubject, entry.Actor.SubjectId));
        Assert.All(page.Items, entry => Assert.Equal(AuditEntryActorKind.User, entry.Actor.Kind));
    }

    [SkippableFact]
    public async Task The_trail_comes_back_newest_first()
    {
        database.SkipWhenUnavailable();
        var mission = await database.ScheduleMission();

        var page = await Read(Query(entityId: mission.Id.ToString()));

        Assert.True(page.Items.Count > 1, "The scenario should have written more than one entry.");
        var times = page.Items.Select(entry => entry.OccurredAtUtc).ToList();
        Assert.Equal(times.OrderByDescending(time => time), times);
    }

    [SkippableFact]
    public async Task The_upper_bound_is_exclusive_and_the_lower_bound_is_inclusive()
    {
        // L-30, measured rather than quoted. An entry stamped exactly "to" is outside the window; the
        // same entry stamped exactly "from" is inside it. That is what lets [a, b) and [b, c) tile the
        // timeline without overlapping or losing a row.
        database.SkipWhenUnavailable();
        var vehicleId = await database.RegisterVehicle();
        var entry = Assert.Single(
            (await Read(Query(entityId: vehicleId.ToString(), category: AuditEntryCategory.BusinessAction))).Items);
        var stamp = entry.OccurredAtUtc;

        // Step by one microsecond, not by one tick. "to: stamp.AddTicks(1)" looks obviously right and is
        // not: audit.entries.occurred_at_utc is a PostgreSQL timestamptz, whose resolution is one
        // microsecond, while a .NET tick is 100 ns — ten times finer. "stamp" comes back already truncated
        // to a microsecond, so stamp + 1 tick truncates back to stamp on the way into the query, "to"
        // becomes equal to the row's own instant, and the exclusive upper bound correctly excludes it. The
        // smallest value this database can tell apart from "stamp" is one microsecond. Do not shrink it.
        var oneMicrosecond = TimeSpan.FromMicroseconds(1);

        var excluded = await Read(Query(entityId: vehicleId.ToString(), category: AuditEntryCategory.BusinessAction, to: stamp));
        var included = await Read(Query(entityId: vehicleId.ToString(), category: AuditEntryCategory.BusinessAction, to: stamp + oneMicrosecond));
        var onTheLowerBound = await Read(Query(entityId: vehicleId.ToString(), category: AuditEntryCategory.BusinessAction, from: stamp));

        // Exactly "to" is outside the window; exactly "from" is inside it.
        Assert.Empty(excluded.Items);
        Assert.Single(included.Items);
        Assert.Single(onTheLowerBound.Items);

        // And therefore two adjacent windows tile the timeline: the entry falls in exactly one of
        // [stamp - 1µs, stamp) and [stamp, stamp + 1µs) — neither lost by both nor counted by both.
        var before = await Read(Query(
            entityId: vehicleId.ToString(),
            category: AuditEntryCategory.BusinessAction,
            from: stamp - oneMicrosecond,
            to: stamp));
        var onAndAfter = await Read(Query(
            entityId: vehicleId.ToString(),
            category: AuditEntryCategory.BusinessAction,
            from: stamp,
            to: stamp + oneMicrosecond));

        Assert.Empty(before.Items);
        Assert.Single(onAndAfter.Items);
    }

    [SkippableFact]
    public async Task A_rejected_attempt_is_readable_with_its_domain_and_code()
    {
        // The challenge's "a rejected operation is audited", read back through the surface an
        // administrator actually uses rather than through the port directly.
        database.SkipWhenUnavailable();
        var mission = await database.ScheduleMission();
        var unknownVehicle = Guid.CreateVersion7();
        var driverId = await database.RegisterDriver();

        await using (var scope = database.Scope())
        {
            var result = await scope.AssignAsync(mission.Id, unknownVehicle, driverId);
            Assert.True(result.IsFailure);
        }

        var page = await Read(Query(entityId: mission.Id.ToString(), outcome: AuditEntryOutcome.Rejected));

        var rejected = Assert.Single(page.Items);
        Assert.Equal(AuditEntryOutcome.Rejected, rejected.Outcome);
        // Two different identifiers that happen to share the word "operations", and they are NOT spelled
        // the same. Do not "correct" one to match the other:
        //   * the error DOMAIN is lowercase — "operations" — because that is the domain the BusinessRule
        //     carries and the one the failure descriptor is built from. The line below is correct as it is.
        //   * the audit MODULE NAME is capitalised — "Operations" — because that is OperationsAudit.Module,
        //     the value the recorder stores in audit.entries.module.
        Assert.Equal(new AuditEntryFailureView("operations", "MISSION_VEHICLE_NOT_FOUND"), rejected.Failure);
        Assert.Equal(OperationsAudit.Module, rejected.Module);
    }

    [SkippableFact]
    public async Task Each_filter_narrows_the_trail_on_its_own_column()
    {
        database.SkipWhenUnavailable();
        var vehicleId = await database.RegisterVehicle();

        var byCategory = await Read(Query(entityId: vehicleId.ToString(), category: AuditEntryCategory.EntityChange));
        var byOutcome = await Read(Query(entityId: vehicleId.ToString(), outcome: AuditEntryOutcome.Succeeded));
        var byActor = await Read(Query(entityId: vehicleId.ToString(), actor: PostgreSqlFixture.ActorSubject));
        var byAnotherActor = await Read(Query(entityId: vehicleId.ToString(), actor: "nobody-with-this-subject"));
        // OperationsAudit.Module, not the literal "operations". With the lowercase literal this page came
        // back empty for two reasons at once — the intended one and a casing typo that matches nothing —
        // and stated only the first. Against the real module name the emptiness proves what it claims:
        // a Fleet vehicle has no Operations entries, so the module filter narrowed on its own column.
        var byAnotherModule = await Read(Query(entityId: vehicleId.ToString(), module: OperationsAudit.Module));

        Assert.All(byCategory.Items, entry => Assert.Equal(AuditEntryCategory.EntityChange, entry.Category));
        Assert.All(byOutcome.Items, entry => Assert.Equal(AuditEntryOutcome.Succeeded, entry.Outcome));
        Assert.NotEmpty(byActor.Items);
        Assert.Empty(byAnotherActor.Items);
        Assert.Empty(byAnotherModule.Items);
    }

    [SkippableFact]
    public async Task Paging_walks_the_trail_without_repeating_or_losing_a_row()
    {
        database.SkipWhenUnavailable();
        var mission = await database.ScheduleMission();

        var whole = await Read(Query(entityId: mission.Id.ToString()));
        Assert.True(whole.Total > 1, "The scenario should have written more than one entry.");

        var first = await Read(Query(entityId: mission.Id.ToString(), number: 1, size: 1));
        var second = await Read(Query(entityId: mission.Id.ToString(), number: 2, size: 1));

        Assert.Equal(whole.Total, first.Total);
        Assert.Equal(1, first.Number);
        Assert.Equal(1, first.Size);
        Assert.Equal(2, second.Number);
        Assert.Single(first.Items);
        Assert.Single(second.Items);
        Assert.NotEqual(first.Items[0], second.Items[0]);
        Assert.Equal(whole.Items[0], first.Items[0]);
        Assert.Equal(whole.Items[1], second.Items[0]);
    }

    [SkippableFact]
    public async Task A_window_that_matches_nothing_is_an_empty_page_and_not_a_failure()
    {
        database.SkipWhenUnavailable();

        var page = await Read(Query(
            from: new DateTimeOffset(1999, 1, 1, 0, 0, 0, TimeSpan.Zero),
            to: new DateTimeOffset(1999, 1, 2, 0, 0, 0, TimeSpan.Zero)));

        Assert.Empty(page.Items);
        Assert.Equal(0, page.Total);
    }
}
