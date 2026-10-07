using Fserp.FleetOperations.Infrastructure.Persistence;
using Fserp.FleetOperations.Modules.Drivers.Application.Commands;
using Fserp.FleetOperations.Modules.Drivers.Application.Ports;
using Fserp.FleetOperations.Modules.Drivers.Application.Views;
using Fserp.FleetOperations.Modules.Drivers.Domain;
using Fserp.FleetOperations.Modules.Fleet.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MPCore.Application.Results;
using MPCore.Application.Time;
using MPCore.Audit;
using MPCore.Domain.Rules;
using MPCore.Persistence.Abstractions;

namespace Fserp.FleetOperations.IntegrationTests.Drivers;

/// <summary>
/// The Drivers schema against a real PostgreSQL: the migration, the unique qualification index (L-18),
/// the <c>xmin</c> concurrency token, the availability predicate translated to SQL, and the audit trail
/// with the driver's name masked.
/// </summary>
/// <remarks>
/// Every test here reports Skipped with the fixture's reason when no PostgreSQL is reachable. None of them
/// passes vacuously, and none of what they assert is proved by the unit suite: <c>xmin</c>, a partial
/// unique index, SQL translation of the availability expression and the audit interceptor are PostgreSQL
/// behaviour.
/// </remarks>
[Collection(PostgreSqlCollection.Name)]
public sealed class DriverPersistenceTests(PostgreSqlFixture database)
{
    private static Task<Result<DriverView>> Register(
        IServiceProvider services,
        string fullName,
        params VehicleType[] types) =>
        RegisterDriverHandler.Handle(
            new RegisterDriver(fullName, types),
            services.GetRequiredService<IDriverRepository>(),
            services.GetRequiredService<IUnitOfWork>(),
            services.GetRequiredService<IClock>(),
            services.GetRequiredService<IBusinessAuditRecorder>(),
            CancellationToken.None);

    private static Task<Result<DriverView>> ChangeStatus(IServiceProvider services, Guid driverId, OperationalStatus status) =>
        ChangeDriverStatusHandler.Handle(
            new ChangeDriverStatus(driverId, status),
            services.GetRequiredService<IDriverRepository>(),
            services.GetRequiredService<IUnitOfWork>(),
            services.GetRequiredService<IBusinessAuditRecorder>(),
            CancellationToken.None);

    private async Task<DriverView> RegisterAndCommit(string fullName, params VehicleType[] types)
    {
        await using var scope = database.Scope();
        var result = await Register(scope.ServiceProvider, fullName, types.Length == 0 ? [VehicleType.Truck] : types);
        Assert.True(result.IsSuccess);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        return result.Value;
    }

    private async Task<IReadOnlyList<AuditEntry>> AuditOf(Guid driverId, AuditOutcome? outcome = null)
    {
        await using var scope = database.Scope();
        var page = await scope.ServiceProvider.GetRequiredService<IAuditQuery>().QueryAsync(
            new AuditQueryFilter { Module = "Drivers", EntityId = driverId.ToString(), Outcome = outcome },
            new AuditPageRequest(1, 50),
            CancellationToken.None);
        return page.Items;
    }

    [SkippableFact]
    public async Task The_migrations_create_the_drivers_schema_with_its_two_tables_and_their_bounds()
    {
        database.SkipWhenUnavailable();
        await using var scope = database.Scope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        var tables = await context.Database
            .SqlQuery<string>($"SELECT tablename AS \"Value\" FROM pg_tables WHERE schemaname = 'drivers' ORDER BY tablename")
            .ToListAsync();
        Assert.Equal(["drivers", "qualifications"], tables);

        // L-17: the column carries the 128-character bound the validator measures.
        var nameType = await context.Database
            .SqlQuery<string>($"SELECT format_type(atttypid, atttypmod) AS \"Value\" FROM pg_attribute WHERE attrelid = 'drivers.drivers'::regclass AND attname = 'full_name'")
            .SingleAsync();
        Assert.Equal("character varying(128)", nameType);
    }

    [SkippableFact]
    public async Task The_qualification_index_is_unique_on_the_driver_and_the_vehicle_type()
    {
        // L-18: the database backstop of DRIVER_QUALIFICATION_DUPLICATE, as the plate's index backstops F-2.
        database.SkipWhenUnavailable();
        await using var scope = database.Scope();

        var definition = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database
            .SqlQuery<string>($"SELECT indexdef AS \"Value\" FROM pg_indexes WHERE schemaname = 'drivers' AND indexname = 'ux_qualifications_driver_id_vehicle_type'")
            .SingleAsync();

        Assert.Contains("CREATE UNIQUE INDEX", definition, StringComparison.Ordinal);
        Assert.Contains("driver_id", definition, StringComparison.Ordinal);
        Assert.Contains("vehicle_type", definition, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task A_registered_driver_is_stored_as_Active_with_its_qualifications_and_read_back_as_its_view()
    {
        database.SkipWhenUnavailable();

        var registered = await RegisterAndCommit("  Ada Lovelace  ", VehicleType.HeavyTruck, VehicleType.Van);

        await using var scope = database.Scope();
        var read = await scope.ServiceProvider.GetRequiredService<IDriverReadModel>().GetAsync(registered.Id, CancellationToken.None);
        Assert.NotNull(read);
        Assert.Equal("Ada Lovelace", read.FullName);
        Assert.Equal(OperationalStatus.Active, read.OperationalStatus);
        Assert.Null(read.CommittedMissionId);
        Assert.Equal([VehicleType.Van, VehicleType.HeavyTruck], read.QualifiedVehicleTypes);

        // Enums are stored as strings (docs/plans/drivers.md, "Persistence").
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await context.Database
            .SqlQuery<string>($"SELECT operational_status AS \"Value\" FROM drivers.drivers WHERE id = {registered.Id}")
            .SingleAsync();
        Assert.Equal("Active", stored);
        var types = await context.Database
            .SqlQuery<string>($"SELECT vehicle_type AS \"Value\" FROM drivers.qualifications WHERE driver_id = {registered.Id} ORDER BY vehicle_type")
            .ToListAsync();
        Assert.Equal(["HeavyTruck", "Van"], types);
    }

    [SkippableFact]
    public async Task An_unknown_driver_reads_as_nothing()
    {
        database.SkipWhenUnavailable();
        await using var scope = database.Scope();

        Assert.Null(await scope.ServiceProvider.GetRequiredService<IDriverReadModel>()
            .GetAsync(Guid.CreateVersion7(), CancellationToken.None));
    }

    [SkippableFact]
    public async Task The_duplicate_rule_is_backstopped_by_the_unique_index()
    {
        // The rule refuses a duplicate first; this proves the database would refuse it even if a future
        // code path did not. Written directly, because no production path can produce the second row.
        database.SkipWhenUnavailable();
        var registered = await RegisterAndCommit("Ada", VehicleType.Van);

        await using var scope = database.Scope();
        var refused = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlAsync(
                $"INSERT INTO drivers.qualifications (id, driver_id, vehicle_type, created_on_utc) VALUES ({Guid.CreateVersion7()}, {registered.Id}, 'Van', now())"));

        Assert.Equal(Npgsql.PostgresErrorCodes.UniqueViolation, refused.SqlState);
        Assert.Equal("ux_qualifications_driver_id_vehicle_type", refused.ConstraintName);
    }

    [SkippableFact]
    public async Task The_available_list_excludes_the_Inactive_and_the_committed_and_translates_to_SQL()
    {
        database.SkipWhenUnavailable();
        var available = await RegisterAndCommit("Available " + Guid.NewGuid().ToString("N")[..8]);
        var inactive = await RegisterAndCommit("Inactive " + Guid.NewGuid().ToString("N")[..8]);
        var committed = await RegisterAndCommit("Committed " + Guid.NewGuid().ToString("N")[..8]);

        await using (var scope = database.Scope())
        {
            Assert.True((await ChangeStatus(scope.ServiceProvider, inactive.Id, OperationalStatus.Inactive)).IsSuccess);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        await using (var scope = database.Scope())
        {
            await scope.ServiceProvider.GetRequiredService<Fserp.FleetOperations.Modules.Drivers.Contracts.IDriverCommitments>()
                .CommitToMissionAsync(committed.Id, Guid.CreateVersion7(), VehicleType.Truck, CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        await using (var check = database.Scope())
        {
            // The predicate ran in PostgreSQL: the status column is text through a converter and the
            // commitment is a nullable uuid, so both comparisons had to translate.
            var ids = (await check.ServiceProvider.GetRequiredService<IDriverReadModel>()
                    .GetAvailableAsync(CancellationToken.None))
                .Select(view => view.Id)
                .ToList();

            Assert.Contains(available.Id, ids);
            Assert.DoesNotContain(inactive.Id, ids);
            Assert.DoesNotContain(committed.Id, ids);
        }
    }

    [SkippableFact]
    public async Task A_lost_update_on_a_driver_is_refused_by_the_xmin_token()
    {
        database.SkipWhenUnavailable();
        var registered = await RegisterAndCommit("Ada");
        await using var first = database.Scope();
        await using var second = database.Scope();
        var firstCopy = (await first.ServiceProvider.GetRequiredService<IDriverRepository>().GetAsync(registered.Id))!;
        var secondCopy = (await second.ServiceProvider.GetRequiredService<IDriverRepository>().GetAsync(registered.Id))!;

        firstCopy.ChangeStatus(OperationalStatus.Inactive);
        await first.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        secondCopy.ChangeStatus(OperationalStatus.Inactive);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            second.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync());
    }

    [SkippableFact]
    public async Task A_commitment_racing_a_deactivation_loses_on_the_drivers_own_token()
    {
        // Decision 2 for a driver: both write the driver row, so the second UPDATE ... WHERE xmin matches
        // zero rows. This is the Drivers half of the race the Assign command will run in round 7.
        database.SkipWhenUnavailable();
        var registered = await RegisterAndCommit("Ada", VehicleType.Truck);
        await using var assigning = database.Scope();
        await using var deactivating = database.Scope();
        var assignedCopy = (await assigning.ServiceProvider.GetRequiredService<IDriverRepository>().GetAsync(registered.Id))!;
        var deactivatedCopy = (await deactivating.ServiceProvider.GetRequiredService<IDriverRepository>().GetAsync(registered.Id))!;

        deactivatedCopy.ChangeStatus(OperationalStatus.Inactive);
        await deactivating.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        assignedCopy.CommitToMission(Guid.CreateVersion7(), VehicleType.Truck);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            assigning.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync());
    }

    [SkippableFact]
    public async Task Registration_records_the_business_action_and_the_entity_change_with_the_name_masked()
    {
        // docs/plans/drivers.md: FullName is personal data and is masked with Redact if included at all.
        database.SkipWhenUnavailable();

        var registered = await RegisterAndCommit("Ada Lovelace", VehicleType.Van);

        var entries = await AuditOf(registered.Id);
        var created = Assert.Single(entries, entry => entry.Category == AuditCategory.EntityChange);
        Assert.Equal("Created", created.Action);
        Assert.Equal("Driver", created.EntityType);
        Assert.Equal("Active", Assert.Single(created.Changes, change => change.Name == "OperationalStatus").After);
        var name = Assert.Single(created.Changes, change => change.Name == "FullName");
        Assert.NotEqual("Ada Lovelace", name.After);
        Assert.DoesNotContain("Ada", name.After ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Lovelace", name.After ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var action = Assert.Single(entries, entry => entry.Category == AuditCategory.BusinessAction);
        Assert.Equal("DriverRegistered", action.Action);
        Assert.Equal(AuditOutcome.Succeeded, action.Outcome);
        Assert.Equal(PostgreSqlFixture.ActorSubject, action.Actor.SubjectId);
    }

    [SkippableFact]
    public async Task The_qualification_rows_are_audited_with_their_vehicle_type()
    {
        // The plan names the qualification types in the entity change policy. They live in child rows, so
        // Qualification is declared under the Drivers module and each row leaves a Created entry.
        database.SkipWhenUnavailable();
        var registered = await RegisterAndCommit("Ada", VehicleType.Van, VehicleType.HeavyTruck);

        await using var scope = database.Scope();
        var page = await scope.ServiceProvider.GetRequiredService<IAuditQuery>().QueryAsync(
            new AuditQueryFilter { Module = "Drivers", EntityType = "Qualification" },
            new AuditPageRequest(1, 200),
            CancellationToken.None);

        var qualificationIds = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database
            .SqlQuery<Guid>($"SELECT id AS \"Value\" FROM drivers.qualifications WHERE driver_id = {registered.Id}")
            .ToListAsync();
        var mine = page.Items.Where(entry => qualificationIds.Select(id => id.ToString()).Contains(entry.EntityId)).ToList();

        Assert.Equal(2, mine.Count);
        Assert.Equal(
            ["HeavyTruck", "Van"],
            mine.Select(entry => Assert.Single(entry.Changes, change => change.Name == "VehicleType").After).Order());
    }

    [SkippableFact]
    public async Task A_status_change_is_stored_and_audited_with_from_and_to()
    {
        database.SkipWhenUnavailable();
        var registered = await RegisterAndCommit("Ada");

        await using (var scope = database.Scope())
        {
            Assert.True((await ChangeStatus(scope.ServiceProvider, registered.Id, OperationalStatus.Inactive)).IsSuccess);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        var entries = await AuditOf(registered.Id);
        var action = Assert.Single(entries, entry => entry.Action == "DriverStatusChanged");
        Assert.Equal(AuditOutcome.Succeeded, action.Outcome);
        Assert.Equal("Active", action.Metadata["from"]);
        Assert.Equal("Inactive", action.Metadata["to"]);
        var updated = Assert.Single(entries, entry => entry.Category == AuditCategory.EntityChange && entry.Action == "Updated");
        Assert.Equal(new AuditFieldChange("OperationalStatus", "Active", "Inactive"), Assert.Single(updated.Changes));
    }

    [SkippableFact]
    public async Task Setting_the_current_status_writes_nothing_and_audits_nothing()
    {
        database.SkipWhenUnavailable();
        var registered = await RegisterAndCommit("Ada");

        await using (var scope = database.Scope())
        {
            Assert.True((await ChangeStatus(scope.ServiceProvider, registered.Id, OperationalStatus.Active)).IsSuccess);
            Assert.False(scope.ServiceProvider.GetRequiredService<AppDbContext>().ChangeTracker.HasChanges());
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        var entries = await AuditOf(registered.Id);
        Assert.DoesNotContain(entries, entry => entry.Action == "DriverStatusChanged");
        Assert.DoesNotContain(entries, entry => entry.Action == "Updated");
    }

    [SkippableFact]
    public async Task A_refused_deactivation_leaves_the_driver_Active_and_a_rejected_attempt_that_survives_the_rollback()
    {
        database.SkipWhenUnavailable();
        var registered = await RegisterAndCommit("Ada", VehicleType.Truck);
        var mission = Guid.CreateVersion7();
        await using (var setup = database.Scope())
        {
            await setup.ServiceProvider.GetRequiredService<Fserp.FleetOperations.Modules.Drivers.Contracts.IDriverCommitments>()
                .CommitToMissionAsync(registered.Id, mission, VehicleType.Truck, CancellationToken.None);
            await setup.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        await using (var scope = database.Scope())
        {
            var refused = await Assert.ThrowsAsync<BusinessRuleValidationException>(() =>
                ChangeStatus(scope.ServiceProvider, registered.Id, OperationalStatus.Inactive));
            Assert.Equal("DRIVER_HAS_MISSION_COMMITMENT", refused.Rule.Code);
            // The middleware would roll back here; the scope is discarded without saving.
        }

        await using (var check = database.Scope())
        {
            var read = await check.ServiceProvider.GetRequiredService<IDriverReadModel>().GetAsync(registered.Id, CancellationToken.None);
            Assert.Equal(OperationalStatus.Active, read!.OperationalStatus);
            Assert.Equal(mission, read.CommittedMissionId);
        }

        var rejected = Assert.Single(await AuditOf(registered.Id, AuditOutcome.Rejected));
        Assert.Equal(AuditCategory.BusinessAction, rejected.Category);
        Assert.Equal("DriverStatusChanged", rejected.Action);
        Assert.Equal(new AuditFailure("drivers", "DRIVER_HAS_MISSION_COMMITMENT"), rejected.Failure);
        Assert.Equal(PostgreSqlFixture.ActorSubject, rejected.Actor.SubjectId);
        Assert.DoesNotContain(await AuditOf(registered.Id, AuditOutcome.Succeeded), entry => entry.Action == "DriverStatusChanged");
    }
}
