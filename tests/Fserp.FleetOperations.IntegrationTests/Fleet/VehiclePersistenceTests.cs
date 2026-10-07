using Fserp.FleetOperations.Api.Hosting;
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

[Collection(PostgreSqlCollection.Name)]
public sealed class VehiclePersistenceTests(PostgreSqlFixture database)
{
    private static string UniquePlate() => "IT-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();

    private static Task<Result<VehicleView>> Register(IServiceProvider services, string plate, decimal capacityKg = 1250.5m) =>
        RegisterVehicleHandler.Handle(
            new RegisterVehicle(plate, VehicleType.Truck, capacityKg),
            services.GetRequiredService<IVehicleRepository>(),
            services.GetRequiredService<IUnitOfWork>(),
            services.GetRequiredService<IClock>(),
            services.GetRequiredService<IBusinessAuditRecorder>(),
            CancellationToken.None);

    private static Task<Result<VehicleView>> ChangeStatus(IServiceProvider services, Guid vehicleId, OperationalStatus status) =>
        ChangeVehicleStatusHandler.Handle(
            new ChangeVehicleStatus(vehicleId, status),
            services.GetRequiredService<IVehicleRepository>(),
            services.GetRequiredService<IUnitOfWork>(),
            services.GetRequiredService<IBusinessAuditRecorder>(),
            CancellationToken.None);

    private async Task<VehicleView> RegisterAndCommit(string plate)
    {
        await using var scope = database.Scope();
        var result = await Register(scope.ServiceProvider, plate);
        Assert.True(result.IsSuccess);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        return result.Value;
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

    [SkippableFact]
    public async Task The_migrations_create_fleet_vehicles_with_a_unique_plate_index()
    {
        database.SkipWhenUnavailable();
        await using var scope = database.Scope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        var definition = await context.Database
            .SqlQuery<string>($"SELECT indexdef AS \"Value\" FROM pg_indexes WHERE schemaname = 'fleet' AND indexname = 'ux_vehicles_plate_number'")
            .SingleAsync();
        Assert.Contains("CREATE UNIQUE INDEX", definition, StringComparison.Ordinal);
        Assert.Contains("(plate_number)", definition, StringComparison.Ordinal);
        // Owner decision (round 1 closure): plate numbers are at most 16 characters, in the column too.
        var plateType = await context.Database
            .SqlQuery<string>($"SELECT format_type(atttypid, atttypmod) AS \"Value\" FROM pg_attribute WHERE attrelid = 'fleet.vehicles'::regclass AND attname = 'plate_number'")
            .SingleAsync();
        Assert.Equal("character varying(16)", plateType);
    }

    [SkippableFact]
    public async Task A_registered_vehicle_is_stored_as_registered_and_read_back_as_its_view()
    {
        database.SkipWhenUnavailable();
        var plate = UniquePlate();

        var registered = await RegisterAndCommit(" " + plate.ToLowerInvariant() + " ");

        await using var scope = database.Scope();
        var read = await scope.ServiceProvider.GetRequiredService<IVehicleReadModel>().GetAsync(registered.Id, CancellationToken.None);
        Assert.Equal(registered, read);
        Assert.Equal(plate, read!.PlateNumber);
        Assert.Equal(OperationalStatus.Active, read.OperationalStatus);
        Assert.Equal(MaintenanceStatus.NotUnderMaintenance, read.MaintenanceStatus);
        Assert.Equal(1250.5m, read.CapacityKg);

        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await context.Database
            .SqlQuery<string>($"SELECT operational_status || '|' || maintenance_status || '|' || vehicle_type AS \"Value\" FROM fleet.vehicles WHERE id = {registered.Id}")
            .SingleAsync();
        Assert.Equal("Active|NotUnderMaintenance|Truck", stored);
    }

    [SkippableFact]
    public async Task An_unknown_vehicle_reads_as_nothing()
    {
        database.SkipWhenUnavailable();
        await using var scope = database.Scope();

        Assert.Null(await scope.ServiceProvider.GetRequiredService<IVehicleReadModel>().GetAsync(Guid.CreateVersion7(), CancellationToken.None));
    }

    [SkippableFact]
    public async Task Registration_records_the_entity_change_with_the_plate_unmasked_and_the_business_action()
    {
        database.SkipWhenUnavailable();
        var plate = UniquePlate();

        var registered = await RegisterAndCommit(plate);

        var entries = await AuditOf(registered.Id);
        var created = Assert.Single(entries, entry => entry.Category == AuditCategory.EntityChange);
        Assert.Equal("Created", created.Action);
        Assert.Equal("Vehicle", created.EntityType);
        Assert.Equal(plate, Assert.Single(created.Changes, change => change.Name == "PlateNumber").After);
        Assert.Equal("1250.5", Assert.Single(created.Changes, change => change.Name == "Capacity").After);
        Assert.Equal("Active", Assert.Single(created.Changes, change => change.Name == "OperationalStatus").After);
        var action = Assert.Single(entries, entry => entry.Category == AuditCategory.BusinessAction);
        Assert.Equal("VehicleRegistered", action.Action);
        Assert.Equal(AuditOutcome.Succeeded, action.Outcome);
        Assert.Equal(PostgreSqlFixture.ActorSubject, action.Actor.SubjectId);
    }

    [SkippableFact]
    public async Task A_plate_already_registered_is_refused_before_anything_is_written()
    {
        database.SkipWhenUnavailable();
        var plate = UniquePlate();
        await RegisterAndCommit(plate);

        await using var scope = database.Scope();
        var result = await Register(scope.ServiceProvider, plate.ToLowerInvariant());

        Assert.True(result.IsFailure);
        Assert.Equal("VEHICLE_PLATE_NUMBER_ALREADY_REGISTERED", result.FailureDescriptor!.Identity.Code);
        Assert.False(scope.ServiceProvider.GetRequiredService<AppDbContext>().ChangeTracker.HasChanges());
    }

    [SkippableFact]
    public async Task Two_racing_registrations_of_one_plate_commit_exactly_one_and_the_other_maps_to_the_409()
    {
        database.SkipWhenUnavailable();
        var plate = UniquePlate();
        await using var first = database.Scope();
        await using var second = database.Scope();

        // Both pass the pre-check, because neither has committed yet.
        Assert.True((await Register(first.ServiceProvider, plate)).IsSuccess);
        Assert.True((await Register(second.ServiceProvider, plate)).IsSuccess);
        await first.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        var refused = await Assert.ThrowsAsync<DbUpdateException>(() =>
            second.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync());

        var failure = UniqueViolations.TryMap(refused);
        Assert.NotNull(failure);
        Assert.Equal("fleet", failure.Identity.Domain);
        Assert.Equal("VEHICLE_PLATE_NUMBER_ALREADY_REGISTERED", failure.Identity.Code);
        Assert.Equal(ErrorCategory.AlreadyExists, failure.Category);

        await using var check = database.Scope();
        var count = await check.ServiceProvider.GetRequiredService<AppDbContext>().Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM fleet.vehicles WHERE plate_number = {plate}")
            .SingleAsync();
        Assert.Equal(1, count);
    }

    [SkippableFact]
    public async Task A_lost_update_is_refused_by_the_xmin_token()
    {
        database.SkipWhenUnavailable();
        var registered = await RegisterAndCommit(UniquePlate());
        await using var first = database.Scope();
        await using var second = database.Scope();
        var firstCopy = (await first.ServiceProvider.GetRequiredService<IVehicleRepository>().GetAsync(registered.Id))!;
        var secondCopy = (await second.ServiceProvider.GetRequiredService<IVehicleRepository>().GetAsync(registered.Id))!;

        firstCopy.ChangeStatus(OperationalStatus.Inactive);
        await first.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        secondCopy.ChangeStatus(OperationalStatus.Inactive);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            second.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync());
    }

    [SkippableFact]
    public async Task A_status_change_is_stored_and_audited_with_from_and_to()
    {
        database.SkipWhenUnavailable();
        var registered = await RegisterAndCommit(UniquePlate());

        await using (var scope = database.Scope())
        {
            var result = await ChangeStatus(scope.ServiceProvider, registered.Id, OperationalStatus.Inactive);
            Assert.True(result.IsSuccess);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        var entries = await AuditOf(registered.Id);
        var action = Assert.Single(entries, entry => entry.Action == "VehicleStatusChanged");
        Assert.Equal(AuditOutcome.Succeeded, action.Outcome);
        Assert.Equal("Active", action.Metadata["from"]);
        Assert.Equal("Inactive", action.Metadata["to"]);
        var updated = Assert.Single(entries, entry => entry.Category == AuditCategory.EntityChange && entry.Action == "Updated");
        var change = Assert.Single(updated.Changes);
        Assert.Equal(new AuditFieldChange("OperationalStatus", "Active", "Inactive"), change);
    }

    [SkippableFact]
    public async Task Setting_the_current_status_writes_nothing_and_audits_nothing()
    {
        database.SkipWhenUnavailable();
        var registered = await RegisterAndCommit(UniquePlate());

        await using (var scope = database.Scope())
        {
            var result = await ChangeStatus(scope.ServiceProvider, registered.Id, OperationalStatus.Active);
            Assert.True(result.IsSuccess);
            Assert.False(scope.ServiceProvider.GetRequiredService<AppDbContext>().ChangeTracker.HasChanges());
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        var entries = await AuditOf(registered.Id);
        Assert.DoesNotContain(entries, entry => entry.Action == "VehicleStatusChanged");
        Assert.DoesNotContain(entries, entry => entry.Action == "Updated");
    }

    [SkippableFact]
    public async Task A_refused_deactivation_leaves_the_vehicle_Active_and_a_rejected_attempt_that_survives_the_rollback()
    {
        database.SkipWhenUnavailable();
        var registered = await RegisterAndCommit(UniquePlate());
        var mission = Guid.CreateVersion7();
        await using (var setup = database.Scope())
        {
            // No production path commits a vehicle in round 1; the state is put in place directly.
            await setup.ServiceProvider.GetRequiredService<AppDbContext>().Database
                .ExecuteSqlAsync($"UPDATE fleet.vehicles SET committed_mission_id = {mission} WHERE id = {registered.Id}");
        }

        await using (var scope = database.Scope())
        {
            var refused = await Assert.ThrowsAsync<BusinessRuleValidationException>(() =>
                ChangeStatus(scope.ServiceProvider, registered.Id, OperationalStatus.Inactive));
            Assert.Equal("VEHICLE_HAS_MISSION_COMMITMENT", refused.Rule.Code);
            // The middleware would roll back here; the scope is discarded without saving.
        }

        await using (var check = database.Scope())
        {
            var read = await check.ServiceProvider.GetRequiredService<IVehicleReadModel>().GetAsync(registered.Id, CancellationToken.None);
            Assert.Equal(OperationalStatus.Active, read!.OperationalStatus);
            Assert.Equal(mission, read.CommittedMissionId);
        }

        var rejected = Assert.Single(await AuditOf(registered.Id, AuditOutcome.Rejected));
        Assert.Equal(AuditCategory.BusinessAction, rejected.Category);
        Assert.Equal("VehicleStatusChanged", rejected.Action);
        Assert.Equal(new AuditFailure("fleet", "VEHICLE_HAS_MISSION_COMMITMENT"), rejected.Failure);
        Assert.Equal(PostgreSqlFixture.ActorSubject, rejected.Actor.SubjectId);
        Assert.DoesNotContain(await AuditOf(registered.Id, AuditOutcome.Succeeded), entry => entry.Action == "VehicleStatusChanged");
    }
}
