using System.Globalization;
using Fserp.FleetOperations.Infrastructure.Persistence;
using Fserp.FleetOperations.Modules.Fleet.Contracts;
using Fserp.FleetOperations.Modules.Operations.Application.Ports;
using Fserp.FleetOperations.Modules.Operations.Domain;
using Fserp.FleetOperations.Modules.Operations.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MPCore.Application.Querying;
using MPCore.Caching.Abstractions;
using MPCore.Persistence.Abstractions;

namespace Fserp.FleetOperations.IntegrationTests.Operations;

/// <summary>
/// The <c>operations.missions</c> table as the migration creates it, and the read model over it: the
/// status stored as a string, the <c>xmin</c> token, the two unique partial indexes with their predicate,
/// and the bounded active page.
/// </summary>
/// <remarks>Skipped with the fixture's reason when no PostgreSQL is reachable.</remarks>
[Collection(PostgreSqlCollection.Name)]
public sealed class MissionPersistenceTests(PostgreSqlFixture database)
{
    private async Task<string> StatusInDatabase(Guid missionId)
    {
        await using var scope = database.Scope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database
            .SqlQuery<string>($"SELECT status AS \"Value\" FROM operations.missions WHERE id = {missionId}")
            .SingleAsync();
    }

    [SkippableFact]
    public async Task A_mission_round_trips_through_the_table_with_every_field_the_view_carries()
    {
        database.SkipWhenUnavailable();
        var created = await database.CreateMission(requiredCapacityKg: 1234.5m, origin: "Tehran", destination: "Mashhad");

        await using var scope = database.Scope();
        var view = await scope.ServiceProvider.GetRequiredService<IMissionReadModel>()
            .GetAsync(created.Id, CancellationToken.None);

        Assert.NotNull(view);
        Assert.Equal(created.Id, view.Id);
        Assert.Equal("Tehran", view.Origin);
        Assert.Equal("Mashhad", view.Destination);
        // numeric without precision: the decimal is stored and read back exactly as given.
        Assert.Equal(1234.5m, view.RequiredCapacityKg);
        Assert.Equal(MissionStatus.Draft, view.Status);
        Assert.Null(view.ScheduledAt);
        Assert.Null(view.AssignedVehicleId);
        Assert.Null(view.AssignedDriverId);
    }

    [SkippableFact]
    public async Task An_unknown_mission_has_no_view()
    {
        database.SkipWhenUnavailable();
        await using var scope = database.Scope();

        Assert.Null(await scope.ServiceProvider.GetRequiredService<IMissionReadModel>()
            .GetAsync(Guid.CreateVersion7(), CancellationToken.None));
    }

    [SkippableFact]
    public async Task The_status_is_stored_as_a_string_so_the_index_predicate_reads_plainly()
    {
        database.SkipWhenUnavailable();
        var mission = await database.ScheduleMission();

        Assert.Equal("Scheduled", await StatusInDatabase(mission.Id));
    }

    [SkippableFact]
    public async Task The_scheduled_time_survives_the_round_trip_as_an_instant()
    {
        database.SkipWhenUnavailable();
        var mission = await database.ScheduleMission();

        await using var scope = database.Scope();
        var view = await scope.ServiceProvider.GetRequiredService<IMissionReadModel>()
            .GetAsync(mission.Id, CancellationToken.None);

        Assert.Equal(MissionScenario.ScheduledAt, view!.ScheduledAt);
    }

    [SkippableFact]
    public async Task Both_unique_partial_indexes_exist_with_the_predicate_the_plan_names()
    {
        // Read from the catalogue rather than from the migration file: this is the index PostgreSQL will
        // actually enforce.
        database.SkipWhenUnavailable();
        await using var scope = database.Scope();
        var definitions = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database
            .SqlQuery<string>($"""
                SELECT indexname || ' :: ' || indexdef AS "Value"
                FROM pg_indexes
                WHERE schemaname = 'operations' AND tablename = 'missions'
                ORDER BY indexname
                """)
            .ToListAsync();

        var vehicleIndex = Assert.Single(definitions, definition =>
            definition.StartsWith(MissionConfiguration.ActiveVehicleUniqueIndex + " ::", StringComparison.Ordinal));
        var driverIndex = Assert.Single(definitions, definition =>
            definition.StartsWith(MissionConfiguration.ActiveDriverUniqueIndex + " ::", StringComparison.Ordinal));

        Assert.Contains("CREATE UNIQUE INDEX", vehicleIndex, StringComparison.Ordinal);
        Assert.Contains("assigned_vehicle_id", vehicleIndex, StringComparison.Ordinal);
        Assert.Contains("WHERE", vehicleIndex, StringComparison.Ordinal);
        Assert.Contains("'Assigned'", vehicleIndex, StringComparison.Ordinal);
        Assert.Contains("'InProgress'", vehicleIndex, StringComparison.Ordinal);
        // Scheduled is not in the predicate: a scheduled mission holds no vehicle yet (O-8).
        Assert.DoesNotContain("'Scheduled'", vehicleIndex, StringComparison.Ordinal);

        Assert.Contains("CREATE UNIQUE INDEX", driverIndex, StringComparison.Ordinal);
        Assert.Contains("assigned_driver_id", driverIndex, StringComparison.Ordinal);
        Assert.Contains("'Assigned'", driverIndex, StringComparison.Ordinal);
        Assert.DoesNotContain("'Scheduled'", driverIndex, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads <c>xmin</c> as text and parses it back to the <see cref="uint"/> an xid is.
    /// </summary>
    /// <remarks>
    /// The column's PostgreSQL type is <c>xid</c>. Asking EF for <c>SqlQuery&lt;uint&gt;</c> does not read
    /// it: the Npgsql provider maps the CLR <see cref="uint"/> to <c>bigint</c>, so the reader is asked for
    /// an <see cref="long"/> over an <c>xid</c> field and throws
    /// <c>InvalidCastException: Reading as 'System.Int64' is not supported for fields having DataTypeName
    /// 'xid'</c>. Casting to <c>text</c> in the SQL is the transport; <see cref="uint.Parse(string,
    /// IFormatProvider?)"/> here is the assertion that what came back really is a transaction identifier
    /// and not an empty string.
    /// </remarks>
    private async Task<uint> TransactionIdOf(Guid missionId)
    {
        await using var scope = database.Scope();
        var text = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database
            .SqlQuery<string>($"SELECT xmin::text AS \"Value\" FROM operations.missions WHERE id = {missionId}")
            .SingleAsync();

        Assert.False(string.IsNullOrWhiteSpace(text), "xmin came back empty; the system column was not read.");
        return uint.Parse(text, CultureInfo.InvariantCulture);
    }

    [SkippableFact]
    public async Task The_table_carries_the_xmin_concurrency_token()
    {
        // xmin is a PostgreSQL system column; the mapping declares it as the row version, and this proves
        // the model really reads it rather than a column of its own.
        database.SkipWhenUnavailable();
        var mission = await database.CreateMission();

        var before = await TransactionIdOf(mission.Id);
        Assert.NotEqual(0u, before);

        await using (var change = database.Scope())
        {
            var repository = change.ServiceProvider.GetRequiredService<IMissionRepository>();
            var loaded = await repository.GetAsync(mission.Id, CancellationToken.None);
            loaded!.Schedule(MissionScenario.ScheduledAt);
            await change.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        var after = await TransactionIdOf(mission.Id);
        Assert.NotEqual(0u, after);

        Assert.NotEqual(before, after);
    }

    [SkippableFact]
    public async Task Two_operators_acting_on_one_mission_lose_the_second_write_at_commit()
    {
        // The plan's reason for xmin on the mission: "two operators acting on one mission". Both load the
        // same row, both change it; the second UPDATE ... WHERE xmin = <read value> matches no row.
        database.SkipWhenUnavailable();
        var mission = await database.CreateMission();

        await using var first = database.Scope();
        await using var second = database.Scope();
        var firstMission = await first.ServiceProvider.GetRequiredService<IMissionRepository>()
            .GetAsync(mission.Id, CancellationToken.None);
        var secondMission = await second.ServiceProvider.GetRequiredService<IMissionRepository>()
            .GetAsync(mission.Id, CancellationToken.None);

        firstMission!.Schedule(MissionScenario.ScheduledAt);
        await first.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();

        secondMission!.Cancel();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            second.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync());

        Assert.Equal("Scheduled", await StatusInDatabase(mission.Id));
    }

    [SkippableFact]
    public async Task The_active_page_contains_exactly_the_three_statuses_O4_names()
    {
        database.SkipWhenUnavailable();
        var vehicleId = await database.RegisterVehicle(capacityKg: 9000m);
        var driverId = await database.RegisterDriver();

        var draft = await database.CreateMission();
        var scheduled = await database.ScheduleMission();
        var assigned = await database.ScheduleMission();
        await database.AssignAndCommit(assigned.Id, vehicleId, driverId);
        var inProgress = assigned;
        await database.StartAndCommit(inProgress.Id);
        var cancelled = await database.CreateMission();
        await database.CancelAndCommit(cancelled.Id);

        await using var scope = database.Scope();
        var page = await scope.ServiceProvider.GetRequiredService<IMissionReadModel>()
            .GetActiveAsync(new PageRequest(1, PageRequest.MaximumSize), CancellationToken.None);
        var ids = page.Items.Select(item => item.Id).ToHashSet();

        Assert.Contains(scheduled.Id, ids);
        Assert.Contains(inProgress.Id, ids);
        Assert.DoesNotContain(draft.Id, ids);
        Assert.DoesNotContain(cancelled.Id, ids);
        Assert.All(page.Items, item => Assert.Contains(item.Status, MissionActivity.ActiveStatuses));
    }

    [SkippableFact]
    public async Task The_active_page_is_bounded_and_its_pages_do_not_overlap()
    {
        database.SkipWhenUnavailable();
        var created = new List<Guid>();
        for (var index = 0; index < 5; index++)
        {
            created.Add((await database.ScheduleMission()).Id);
        }

        await using var scope = database.Scope();
        var missions = scope.ServiceProvider.GetRequiredService<IMissionReadModel>();
        var first = await missions.GetActiveAsync(new PageRequest(1, 2), CancellationToken.None);
        var secondPage = await missions.GetActiveAsync(new PageRequest(2, 2), CancellationToken.None);

        Assert.Equal(2, first.Items.Count);
        Assert.Equal(2, secondPage.Items.Count);
        Assert.Equal(first.Total, secondPage.Total);
        Assert.True(first.Total >= created.Count);
        // Ordered by a version 7 identity, so one caller's pages neither overlap nor skip.
        Assert.Empty(first.Items.Select(item => item.Id).Intersect(secondPage.Items.Select(item => item.Id)));
    }

    [SkippableFact]
    public async Task The_read_model_answers_without_any_cache_adapter_in_the_container()
    {
        // docs/plans/operations.md: both queries are "Not cached". The strongest form of that here: the
        // composition registers no ICache and no IReadThroughCache, and the reads still answer.
        database.SkipWhenUnavailable();
        var mission = await database.ScheduleMission();
        await using var scope = database.Scope();
        Assert.Null(scope.ServiceProvider.GetService<ICache>());
        Assert.Null(scope.ServiceProvider.GetService<IReadThroughCache>());

        var missions = scope.ServiceProvider.GetRequiredService<IMissionReadModel>();

        Assert.NotNull(await missions.GetAsync(mission.Id, CancellationToken.None));
        Assert.NotEmpty((await missions.GetActiveAsync(PageRequest.First, CancellationToken.None)).Items);
    }

    [SkippableFact]
    public async Task A_read_leaves_nothing_for_a_unit_of_work_to_commit()
    {
        database.SkipWhenUnavailable();
        var mission = await database.ScheduleMission();
        await using var scope = database.Scope();

        await scope.ServiceProvider.GetRequiredService<IMissionReadModel>().GetAsync(mission.Id, CancellationToken.None);
        await scope.ServiceProvider.GetRequiredService<IMissionReadModel>().GetActiveAsync(PageRequest.First, CancellationToken.None);

        Assert.False(scope.ServiceProvider.GetRequiredService<AppDbContext>().ChangeTracker.HasChanges());
    }

    [SkippableFact]
    public async Task The_whole_lifecycle_commits_through_the_three_modules_tables()
    {
        // The end-to-end shape of the slice against the real schema: create, schedule, assign (which
        // writes three tables in one transaction), start, complete (which releases both resources).
        database.SkipWhenUnavailable();
        var vehicleId = await database.RegisterVehicle(capacityKg: 9000m, type: VehicleType.HeavyTruck);
        var driverId = await database.RegisterDriver(VehicleType.HeavyTruck);
        var mission = await database.ScheduleMission(requiredCapacityKg: 8000m);

        await database.AssignAndCommit(mission.Id, vehicleId, driverId);
        Assert.Equal("Assigned", await StatusInDatabase(mission.Id));
        Assert.Equal(mission.Id, await CommittedMissionOfVehicle(vehicleId));
        Assert.Equal(mission.Id, await CommittedMissionOfDriver(driverId));

        await database.StartAndCommit(mission.Id);
        Assert.Equal("InProgress", await StatusInDatabase(mission.Id));
        // Start touches neither Fleet nor Drivers: both commitments are untouched.
        Assert.Equal(mission.Id, await CommittedMissionOfVehicle(vehicleId));
        Assert.Equal(mission.Id, await CommittedMissionOfDriver(driverId));

        await database.CompleteAndCommit(mission.Id);
        Assert.Equal("Completed", await StatusInDatabase(mission.Id));
        Assert.Null(await CommittedMissionOfVehicle(vehicleId));
        Assert.Null(await CommittedMissionOfDriver(driverId));
    }

    [SkippableFact]
    public async Task Cancelling_an_assigned_mission_releases_both_resources_and_cancelling_a_draft_releases_nothing()
    {
        // L-24 against the database: the two paths of Cancel, told apart by what the other two tables say.
        database.SkipWhenUnavailable();
        var vehicleId = await database.RegisterVehicle(capacityKg: 9000m);
        var driverId = await database.RegisterDriver();
        var assigned = await database.ScheduleMission();
        await database.AssignAndCommit(assigned.Id, vehicleId, driverId);

        await database.CancelAndCommit(assigned.Id);

        Assert.Equal("Cancelled", await StatusInDatabase(assigned.Id));
        Assert.Null(await CommittedMissionOfVehicle(vehicleId));
        Assert.Null(await CommittedMissionOfDriver(driverId));

        // The Draft path: nothing was committed, so nothing is released — and the command still succeeds,
        // which it could not if it called the release ports.
        var draft = await database.CreateMission();
        await database.CancelAndCommit(draft.Id);

        Assert.Equal("Cancelled", await StatusInDatabase(draft.Id));
    }

    private async Task<Guid?> CommittedMissionOfVehicle(Guid vehicleId)
    {
        await using var scope = database.Scope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database
            .SqlQuery<Guid?>($"SELECT committed_mission_id AS \"Value\" FROM fleet.vehicles WHERE id = {vehicleId}")
            .SingleAsync();
    }

    private async Task<Guid?> CommittedMissionOfDriver(Guid driverId)
    {
        await using var scope = database.Scope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database
            .SqlQuery<Guid?>($"SELECT committed_mission_id AS \"Value\" FROM drivers.drivers WHERE id = {driverId}")
            .SingleAsync();
    }
}
