using Fserp.FleetOperations.Infrastructure.Persistence;
using Fserp.FleetOperations.Modules.Fleet.Application.Commands;
using Fserp.FleetOperations.Modules.Fleet.Application.Ports;
using Fserp.FleetOperations.Modules.Fleet.Application.Views;
using Fserp.FleetOperations.Modules.Fleet.Contracts;
using Fserp.FleetOperations.Modules.Fleet.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MPCore.Application.Results;
using MPCore.Application.Time;
using MPCore.Audit;
using MPCore.Domain.Rules;
using MPCore.Persistence.Abstractions;

namespace Fserp.FleetOperations.IntegrationTests.Fleet;

/// <summary>
/// The round 2 and round 3 database guarantees against real PostgreSQL: the maintenance column really
/// changes, the audit rows are really written (including the rejected attempt that survives a rollback),
/// and the availability predicate is really translated to SQL rather than evaluated in memory.
/// Every test skips with <see cref="PostgreSqlFixture.UnavailableReason"/> when no PostgreSQL is reachable.
/// </summary>
[Collection(PostgreSqlCollection.Name)]
public sealed class MaintenanceAndAvailabilityPersistenceTests(PostgreSqlFixture database)
{
    private static string UniquePlate() => "MA-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();

    private static Task<Result<VehicleView>> Register(IServiceProvider services, string plate, decimal capacityKg = 1000m) =>
        RegisterVehicleHandler.Handle(
            new RegisterVehicle(plate, VehicleType.Truck, capacityKg),
            services.GetRequiredService<IVehicleRepository>(),
            services.GetRequiredService<IUnitOfWork>(),
            services.GetRequiredService<IClock>(),
            services.GetRequiredService<IBusinessAuditRecorder>(),
            CancellationToken.None);

    private static Task<Result<VehicleView>> Start(IServiceProvider services, Guid vehicleId) =>
        StartMaintenanceHandler.Handle(
            new StartMaintenance(vehicleId),
            services.GetRequiredService<IVehicleRepository>(),
            services.GetRequiredService<IUnitOfWork>(),
            services.GetRequiredService<IBusinessAuditRecorder>(),
            CancellationToken.None);

    private static Task<Result<VehicleView>> Complete(IServiceProvider services, Guid vehicleId) =>
        CompleteMaintenanceHandler.Handle(
            new CompleteMaintenance(vehicleId),
            services.GetRequiredService<IVehicleRepository>(),
            services.GetRequiredService<IUnitOfWork>(),
            services.GetRequiredService<IBusinessAuditRecorder>(),
            CancellationToken.None);

    private async Task<VehicleView> RegisterAndCommit(string plate, decimal capacityKg = 1000m)
    {
        await using var scope = database.Scope();
        var result = await Register(scope.ServiceProvider, plate, capacityKg);
        Assert.True(result.IsSuccess);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        return result.Value;
    }

    private async Task StartAndCommit(Guid vehicleId)
    {
        await using var scope = database.Scope();
        Assert.True((await Start(scope.ServiceProvider, vehicleId)).IsSuccess);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
    }

    private async Task<IReadOnlyList<AuditEntry>> AuditOf(Guid vehicleId, AuditOutcome? outcome = null)
    {
        await using var scope = database.Scope();
        var page = await scope.ServiceProvider.GetRequiredService<IAuditQuery>().QueryAsync(
            new AuditQueryFilter { Module = "Fleet", EntityId = vehicleId.ToString(), Outcome = outcome },
            new AuditPageRequest(1, 50),
            CancellationToken.None);
        return page.Items;
    }

    private async Task<string> MaintenanceColumnOf(Guid vehicleId)
    {
        await using var scope = database.Scope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database
            .SqlQuery<string>($"SELECT maintenance_status AS \"Value\" FROM fleet.vehicles WHERE id = {vehicleId}")
            .SingleAsync();
    }

    [SkippableFact]
    public async Task Starting_maintenance_writes_the_maintenance_column_and_audits_the_action()
    {
        database.SkipWhenUnavailable();
        var registered = await RegisterAndCommit(UniquePlate());

        await StartAndCommit(registered.Id);

        Assert.Equal("UnderMaintenance", await MaintenanceColumnOf(registered.Id));
        var entries = await AuditOf(registered.Id);
        var action = Assert.Single(entries, entry => entry.Action == "MaintenanceStarted");
        Assert.Equal(AuditCategory.BusinessAction, action.Category);
        Assert.Equal(AuditOutcome.Succeeded, action.Outcome);
        Assert.Equal(PostgreSqlFixture.ActorSubject, action.Actor.SubjectId);
        // F-1: only the maintenance field is written, so the entity-change row names only that property.
        var updated = Assert.Single(entries, entry => entry.Category == AuditCategory.EntityChange && entry.Action == "Updated");
        Assert.Equal(
            new AuditFieldChange("MaintenanceStatus", "NotUnderMaintenance", "UnderMaintenance"),
            Assert.Single(updated.Changes));
    }

    [SkippableFact]
    public async Task Completing_maintenance_writes_only_the_maintenance_column_back()
    {
        database.SkipWhenUnavailable();
        var registered = await RegisterAndCommit(UniquePlate());
        await StartAndCommit(registered.Id);

        await using (var scope = database.Scope())
        {
            Assert.True((await Complete(scope.ServiceProvider, registered.Id)).IsSuccess);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        Assert.Equal("NotUnderMaintenance", await MaintenanceColumnOf(registered.Id));
        await using var check = database.Scope();
        var read = await check.ServiceProvider.GetRequiredService<IVehicleReadModel>().GetAsync(registered.Id, CancellationToken.None);
        Assert.Equal(OperationalStatus.Active, read!.OperationalStatus);
        Assert.Equal(MaintenanceStatus.NotUnderMaintenance, read.MaintenanceStatus);
        var completed = Assert.Single(await AuditOf(registered.Id), entry => entry.Action == "MaintenanceCompleted");
        Assert.Equal(AuditOutcome.Succeeded, completed.Outcome);
    }

    [SkippableFact]
    public async Task An_Inactive_vehicle_may_start_maintenance_and_stays_Inactive_afterwards()
    {
        // F-4 and F-1 together, against the real columns.
        database.SkipWhenUnavailable();
        var registered = await RegisterAndCommit(UniquePlate());
        await using (var scope = database.Scope())
        {
            var vehicle = await scope.ServiceProvider.GetRequiredService<IVehicleRepository>().GetAsync(registered.Id);
            vehicle!.ChangeStatus(OperationalStatus.Inactive);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        await StartAndCommit(registered.Id);
        await using (var scope = database.Scope())
        {
            Assert.True((await Complete(scope.ServiceProvider, registered.Id)).IsSuccess);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        await using var check = database.Scope();
        var read = await check.ServiceProvider.GetRequiredService<IVehicleReadModel>().GetAsync(registered.Id, CancellationToken.None);
        Assert.Equal(OperationalStatus.Inactive, read!.OperationalStatus);
        Assert.Equal(MaintenanceStatus.NotUnderMaintenance, read.MaintenanceStatus);
    }

    [SkippableFact]
    public async Task A_refused_start_leaves_the_column_alone_and_the_rejected_attempt_survives_the_rollback()
    {
        database.SkipWhenUnavailable();
        var registered = await RegisterAndCommit(UniquePlate());
        var mission = Guid.CreateVersion7();
        await using (var setup = database.Scope())
        {
            // No production path commits a vehicle yet; the state is put in place directly.
            await setup.ServiceProvider.GetRequiredService<AppDbContext>().Database
                .ExecuteSqlAsync($"UPDATE fleet.vehicles SET committed_mission_id = {mission} WHERE id = {registered.Id}");
        }

        await using (var scope = database.Scope())
        {
            var refused = await Assert.ThrowsAsync<BusinessRuleValidationException>(() => Start(scope.ServiceProvider, registered.Id));
            Assert.Equal("VEHICLE_HAS_MISSION_COMMITMENT", refused.Rule.Code);
            // The middleware would roll back here; the scope is discarded without saving.
        }

        Assert.Equal("NotUnderMaintenance", await MaintenanceColumnOf(registered.Id));
        var rejected = Assert.Single(await AuditOf(registered.Id, AuditOutcome.Rejected));
        Assert.Equal("MaintenanceStarted", rejected.Action);
        Assert.Equal(new AuditFailure("fleet", "VEHICLE_HAS_MISSION_COMMITMENT"), rejected.Failure);
        Assert.DoesNotContain(await AuditOf(registered.Id, AuditOutcome.Succeeded), entry => entry.Action == "MaintenanceStarted");
    }

    [SkippableFact]
    public async Task Starting_maintenance_twice_is_refused_and_the_attempt_is_recorded()
    {
        database.SkipWhenUnavailable();
        var registered = await RegisterAndCommit(UniquePlate());
        await StartAndCommit(registered.Id);

        await using (var scope = database.Scope())
        {
            var refused = await Assert.ThrowsAsync<BusinessRuleValidationException>(() => Start(scope.ServiceProvider, registered.Id));
            Assert.Equal("VEHICLE_ALREADY_UNDER_MAINTENANCE", refused.Rule.Code);
        }

        Assert.Equal("UnderMaintenance", await MaintenanceColumnOf(registered.Id));
        var rejected = Assert.Single(await AuditOf(registered.Id, AuditOutcome.Rejected));
        Assert.Equal("MaintenanceStarted", rejected.Action);
        Assert.Equal(new AuditFailure("fleet", "VEHICLE_ALREADY_UNDER_MAINTENANCE"), rejected.Failure);
    }

    [SkippableFact]
    public async Task Completing_maintenance_that_was_not_started_is_refused_and_records_no_attempt()
    {
        // fleet.md requires no rejected-attempt audit for Complete Maintenance.
        database.SkipWhenUnavailable();
        var registered = await RegisterAndCommit(UniquePlate());

        await using (var scope = database.Scope())
        {
            var refused = await Assert.ThrowsAsync<BusinessRuleValidationException>(() => Complete(scope.ServiceProvider, registered.Id));
            Assert.Equal("VEHICLE_NOT_UNDER_MAINTENANCE", refused.Rule.Code);
        }

        Assert.Empty(await AuditOf(registered.Id, AuditOutcome.Rejected));
    }

    [SkippableFact]
    public async Task The_availability_predicate_is_translated_to_SQL_and_excludes_each_unavailable_vehicle()
    {
        database.SkipWhenUnavailable();
        var available = await RegisterAndCommit(UniquePlate(), 1500m);
        var inactive = await RegisterAndCommit(UniquePlate());
        var maintained = await RegisterAndCommit(UniquePlate());
        var committed = await RegisterAndCommit(UniquePlate());

        await using (var scope = database.Scope())
        {
            var vehicle = await scope.ServiceProvider.GetRequiredService<IVehicleRepository>().GetAsync(inactive.Id);
            vehicle!.ChangeStatus(OperationalStatus.Inactive);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        await StartAndCommit(maintained.Id);

        await using (var setup = database.Scope())
        {
            await setup.ServiceProvider.GetRequiredService<AppDbContext>().Database
                .ExecuteSqlAsync($"UPDATE fleet.vehicles SET committed_mission_id = {Guid.CreateVersion7()} WHERE id = {committed.Id}");
        }

        await using var read = database.Scope();
        var list = await read.ServiceProvider.GetRequiredService<IVehicleReadModel>().GetAvailableAsync(CancellationToken.None);

        var ids = list.Select(row => row.Id).ToHashSet();
        Assert.Contains(available.Id, ids);
        Assert.DoesNotContain(inactive.Id, ids);
        Assert.DoesNotContain(maintained.Id, ids);
        Assert.DoesNotContain(committed.Id, ids);
        var row = Assert.Single(list, candidate => candidate.Id == available.Id);
        Assert.Equal(available.PlateNumber, row.PlateNumber);
        Assert.Equal(VehicleType.Truck, row.VehicleType);
        Assert.Equal(1500m, row.CapacityKg);
    }

    [SkippableFact]
    public async Task A_vehicle_rejoins_the_available_list_when_maintenance_completes()
    {
        database.SkipWhenUnavailable();
        var registered = await RegisterAndCommit(UniquePlate());
        await StartAndCommit(registered.Id);

        await using (var scope = database.Scope())
        {
            var list = await scope.ServiceProvider.GetRequiredService<IVehicleReadModel>().GetAvailableAsync(CancellationToken.None);
            Assert.DoesNotContain(registered.Id, list.Select(row => row.Id));
            Assert.True((await Complete(scope.ServiceProvider, registered.Id)).IsSuccess);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        await using var check = database.Scope();
        var after = await check.ServiceProvider.GetRequiredService<IVehicleReadModel>().GetAvailableAsync(CancellationToken.None);
        Assert.Contains(registered.Id, after.Select(row => row.Id));
    }

    [SkippableFact]
    public async Task The_available_read_leaves_nothing_tracked_for_a_unit_of_work_to_commit()
    {
        // A query only reads: AsNoTracking means no change can escape through a later SaveChanges.
        database.SkipWhenUnavailable();
        await RegisterAndCommit(UniquePlate());

        await using var scope = database.Scope();
        await scope.ServiceProvider.GetRequiredService<IVehicleReadModel>().GetAvailableAsync(CancellationToken.None);

        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(context.ChangeTracker.Entries());
        Assert.False(context.ChangeTracker.HasChanges());
    }

    [SkippableFact]
    public async Task Maintenance_needs_no_schema_change_so_no_migration_is_pending()
    {
        // F-4: maintenance carries no data and maintenance_status already exists, so round 2 added no
        // migration. This fails if a model change was made without one.
        database.SkipWhenUnavailable();
        await using var scope = database.Scope();

        Assert.Empty(await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.GetPendingMigrationsAsync());
    }
}
