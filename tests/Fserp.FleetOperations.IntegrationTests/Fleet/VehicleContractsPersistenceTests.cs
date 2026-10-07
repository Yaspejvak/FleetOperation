using Fserp.FleetOperations.Infrastructure.Persistence;
using Fserp.FleetOperations.Modules.Fleet.Application;
using Fserp.FleetOperations.Modules.Fleet.Application.Commands;
using Fserp.FleetOperations.Modules.Fleet.Application.Ports;
using Fserp.FleetOperations.Modules.Fleet.Application.Views;
using Fserp.FleetOperations.Modules.Fleet.Contracts;
using Fserp.FleetOperations.Modules.Fleet.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MPCore.Application.Time;
using MPCore.Audit;
using MPCore.Caching.Abstractions;
using MPCore.Domain.Rules;
using MPCore.Persistence.Abstractions;

namespace Fserp.FleetOperations.IntegrationTests.Fleet;

/// <summary>
/// The two Fleet Contracts ports against a real PostgreSQL (docs/architecture.md, decisions 1, 2 and 4):
/// the snapshot is read from the database and never from the cache, and the commitment port writes nothing
/// of its own.
/// </summary>
/// <remarks>
/// Skipped with the fixture's reason when no PostgreSQL is reachable. The fixture registers no cache
/// adapter at all, which is itself part of the evidence: <c>VehicleAvailabilityReader</c> resolves and
/// answers without one, so it cannot be reading through a cache.
/// </remarks>
[Collection(PostgreSqlCollection.Name)]
public sealed class VehicleContractsPersistenceTests(PostgreSqlFixture database)
{
    private static string UniquePlate() => "CT-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();

    private async Task<VehicleView> RegisterAndCommit(decimal capacityKg = 1000m)
    {
        await using var scope = database.Scope();
        var result = await RegisterVehicleHandler.Handle(
            new RegisterVehicle(UniquePlate(), VehicleType.Truck, capacityKg),
            scope.ServiceProvider.GetRequiredService<IVehicleRepository>(),
            scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
            scope.ServiceProvider.GetRequiredService<IClock>(),
            scope.ServiceProvider.GetRequiredService<IBusinessAuditRecorder>(),
            CancellationToken.None);
        Assert.True(result.IsSuccess);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        return result.Value;
    }

    private async Task<Guid?> CommittedMissionInDatabase(Guid vehicleId)
    {
        await using var scope = database.Scope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database
            .SqlQuery<Guid?>($"SELECT committed_mission_id AS \"Value\" FROM fleet.vehicles WHERE id = {vehicleId}")
            .SingleAsync();
    }

    [SkippableFact]
    public async Task The_availability_reader_resolves_without_any_cache_adapter_in_the_container()
    {
        // Decision 4 lists IVehicleAvailabilityReader under "Never cached". The strongest form of that
        // here: the composition has no ICache and no IReadThroughCache registered, and the reader still
        // resolves and answers. A reader that consulted a cache could not.
        database.SkipWhenUnavailable();
        var registered = await RegisterAndCommit();
        await using var scope = database.Scope();
        Assert.Null(scope.ServiceProvider.GetService<ICache>());
        Assert.Null(scope.ServiceProvider.GetService<IReadThroughCache>());

        var snapshot = await scope.ServiceProvider.GetRequiredService<IVehicleAvailabilityReader>()
            .GetAsync(registered.Id, CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal(registered.Id, snapshot.VehicleId);
        Assert.Equal(VehicleType.Truck, snapshot.VehicleType);
        Assert.Equal(1000m, snapshot.CapacityKg);
        Assert.True(snapshot.IsActive);
        Assert.False(snapshot.IsUnderMaintenance);
        Assert.Null(snapshot.CommittedMissionId);
    }

    [SkippableFact]
    public async Task The_snapshot_reflects_the_row_as_it_is_now_not_as_a_list_read_earlier_saw_it()
    {
        database.SkipWhenUnavailable();
        var registered = await RegisterAndCommit();

        await using (var maintenance = database.Scope())
        {
            var vehicle = (await maintenance.ServiceProvider.GetRequiredService<IVehicleRepository>().GetAsync(registered.Id))!;
            vehicle.StartMaintenance();
            await maintenance.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        await using var scope = database.Scope();
        var snapshot = await scope.ServiceProvider.GetRequiredService<IVehicleAvailabilityReader>()
            .GetAsync(registered.Id, CancellationToken.None);

        // The cached available list may be up to 30 s stale (F-6); this answer never is.
        Assert.True(snapshot!.IsUnderMaintenance);
    }

    [SkippableFact]
    public async Task An_unknown_vehicle_has_no_snapshot()
    {
        database.SkipWhenUnavailable();
        await using var scope = database.Scope();

        Assert.Null(await scope.ServiceProvider.GetRequiredService<IVehicleAvailabilityReader>()
            .GetAsync(Guid.CreateVersion7(), CancellationToken.None));
    }

    [SkippableFact]
    public async Task The_commitment_port_does_not_save_so_nothing_is_in_the_table_until_the_caller_commits()
    {
        database.SkipWhenUnavailable();
        var registered = await RegisterAndCommit();
        var mission = Guid.CreateVersion7();

        await using (var scope = database.Scope())
        {
            await scope.ServiceProvider.GetRequiredService<IVehicleCommitments>()
                .CommitToMissionAsync(registered.Id, mission, 500m, CancellationToken.None);

            Assert.True(scope.ServiceProvider.GetRequiredService<AppDbContext>().ChangeTracker.HasChanges());
            Assert.Null(await CommittedMissionInDatabase(registered.Id));
        }

        // The scope was discarded without saving, exactly as a rolled-back Assign would.
        Assert.Null(await CommittedMissionInDatabase(registered.Id));
    }

    [SkippableFact]
    public async Task The_callers_unit_of_work_is_what_commits_the_commitment()
    {
        database.SkipWhenUnavailable();
        var registered = await RegisterAndCommit();
        var mission = Guid.CreateVersion7();

        await using (var scope = database.Scope())
        {
            await scope.ServiceProvider.GetRequiredService<IVehicleCommitments>()
                .CommitToMissionAsync(registered.Id, mission, 500m, CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        Assert.Equal(mission, await CommittedMissionInDatabase(registered.Id));
        // The commitment makes the vehicle unavailable, read straight from the database.
        await using var check = database.Scope();
        var snapshot = await check.ServiceProvider.GetRequiredService<IVehicleAvailabilityReader>()
            .GetAsync(registered.Id, CancellationToken.None);
        Assert.Equal(mission, snapshot!.CommittedMissionId);
        Assert.DoesNotContain(
            await check.ServiceProvider.GetRequiredService<IVehicleReadModel>().GetAvailableAsync(CancellationToken.None),
            view => view.Id == registered.Id);
    }

    [SkippableFact]
    public async Task A_refused_commitment_writes_nothing_at_all()
    {
        database.SkipWhenUnavailable();
        var registered = await RegisterAndCommit(capacityKg: 100m);

        await using (var scope = database.Scope())
        {
            var refused = await Assert.ThrowsAsync<BusinessRuleValidationException>(() =>
                scope.ServiceProvider.GetRequiredService<IVehicleCommitments>()
                    .CommitToMissionAsync(registered.Id, Guid.CreateVersion7(), 5000m, CancellationToken.None));

            Assert.Equal("VEHICLE_CAPACITY_INSUFFICIENT", refused.Rule.Code);
            Assert.False(scope.ServiceProvider.GetRequiredService<AppDbContext>().ChangeTracker.HasChanges());
        }

        Assert.Null(await CommittedMissionInDatabase(registered.Id));
    }

    [SkippableFact]
    public async Task A_commitment_racing_a_maintenance_start_loses_on_the_vehicles_own_token()
    {
        // Decision 2, the write-skew case the readers alone could not have caught: both write the vehicle
        // row, so the second UPDATE ... WHERE xmin matches zero rows and EF Core raises the conflict.
        database.SkipWhenUnavailable();
        var registered = await RegisterAndCommit();
        await using var assigning = database.Scope();
        await using var maintaining = database.Scope();
        var assignedCopy = (await assigning.ServiceProvider.GetRequiredService<IVehicleRepository>().GetAsync(registered.Id))!;
        var maintainedCopy = (await maintaining.ServiceProvider.GetRequiredService<IVehicleRepository>().GetAsync(registered.Id))!;

        maintainedCopy.StartMaintenance();
        await maintaining.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        assignedCopy.CommitToMission(Guid.CreateVersion7(), 10m);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            assigning.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync());
    }

    [SkippableFact]
    public async Task A_request_arriving_after_the_first_committed_sees_the_vehicle_as_unavailable()
    {
        // The other half of decision 2's narrative: not a race, just a later caller.
        database.SkipWhenUnavailable();
        var registered = await RegisterAndCommit();
        var mission = Guid.CreateVersion7();
        await using (var first = database.Scope())
        {
            await first.ServiceProvider.GetRequiredService<IVehicleCommitments>()
                .CommitToMissionAsync(registered.Id, mission, 10m, CancellationToken.None);
            await first.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        await using var second = database.Scope();
        var refused = await Assert.ThrowsAsync<BusinessRuleValidationException>(() =>
            second.ServiceProvider.GetRequiredService<IVehicleCommitments>()
                .CommitToMissionAsync(registered.Id, Guid.CreateVersion7(), 10m, CancellationToken.None));

        Assert.Equal("fleet", refused.Rule.ErrorDomain);
        Assert.Equal("VEHICLE_NOT_AVAILABLE", refused.Rule.Code);
        Assert.Equal(mission, await CommittedMissionInDatabase(registered.Id));
    }

    [SkippableFact]
    public async Task The_commitment_is_captured_in_the_entity_change_audit()
    {
        database.SkipWhenUnavailable();
        var registered = await RegisterAndCommit();
        var mission = Guid.CreateVersion7();

        await using (var scope = database.Scope())
        {
            await scope.ServiceProvider.GetRequiredService<IVehicleCommitments>()
                .CommitToMissionAsync(registered.Id, mission, 10m, CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        await using var check = database.Scope();
        var page = await check.ServiceProvider.GetRequiredService<IAuditQuery>().QueryAsync(
            new AuditQueryFilter { Module = FleetAudit.Module, EntityId = registered.Id.ToString(), Category = AuditCategory.EntityChange },
            new AuditPageRequest(1, 50),
            CancellationToken.None);

        var updated = Assert.Single(page.Items, entry => entry.Action == "Updated");
        Assert.Equal(mission.ToString(), Assert.Single(updated.Changes, field => field.Name == "CommittedMissionId").After);
    }
}
