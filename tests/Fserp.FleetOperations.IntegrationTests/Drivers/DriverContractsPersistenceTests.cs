using Fserp.FleetOperations.Infrastructure.Persistence;
using Fserp.FleetOperations.Modules.Drivers.Application.Commands;
using Fserp.FleetOperations.Modules.Drivers.Application.Ports;
using Fserp.FleetOperations.Modules.Drivers.Application.Views;
using Fserp.FleetOperations.Modules.Drivers.Contracts;
using Fserp.FleetOperations.Modules.Drivers.Domain;
using Fserp.FleetOperations.Modules.Fleet.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MPCore.Application.Time;
using MPCore.Audit;
using MPCore.Domain.Rules;
using MPCore.Persistence.Abstractions;

namespace Fserp.FleetOperations.IntegrationTests.Drivers;

/// <summary>
/// The two Drivers Contracts ports against a real PostgreSQL (docs/architecture.md, decision 1): the
/// snapshot comes from the database at the moment of the call, and the commitment port writes nothing of
/// its own — the caller's unit of work commits it, or nothing is committed.
/// </summary>
/// <remarks>
/// Skipped with the fixture's reason when no PostgreSQL is reachable. "Does not save" can be stated in a
/// unit test against a fake repository, but "and therefore nothing is in the table until the caller
/// commits" can only be counted here.
/// </remarks>
[Collection(PostgreSqlCollection.Name)]
public sealed class DriverContractsPersistenceTests(PostgreSqlFixture database)
{
    private async Task<DriverView> RegisterAndCommit(params VehicleType[] types)
    {
        await using var scope = database.Scope();
        var result = await RegisterDriverHandler.Handle(
            new RegisterDriver("Driver " + Guid.NewGuid().ToString("N")[..8], types.Length == 0 ? [VehicleType.Truck] : types),
            scope.ServiceProvider.GetRequiredService<IDriverRepository>(),
            scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
            scope.ServiceProvider.GetRequiredService<IClock>(),
            scope.ServiceProvider.GetRequiredService<IBusinessAuditRecorder>(),
            CancellationToken.None);
        Assert.True(result.IsSuccess);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        return result.Value;
    }

    private async Task<Guid?> CommittedMissionInDatabase(Guid driverId)
    {
        await using var scope = database.Scope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database
            .SqlQuery<Guid?>($"SELECT committed_mission_id AS \"Value\" FROM drivers.drivers WHERE id = {driverId}")
            .SingleAsync();
    }

    [SkippableFact]
    public async Task The_snapshot_is_read_from_PostgreSQL_and_reflects_the_row_as_it_is_now()
    {
        database.SkipWhenUnavailable();
        var registered = await RegisterAndCommit(VehicleType.Van, VehicleType.HeavyTruck);

        await using (var read = database.Scope())
        {
            var snapshot = await read.ServiceProvider.GetRequiredService<IDriverEligibilityReader>()
                .GetAsync(registered.Id, CancellationToken.None);

            Assert.NotNull(snapshot);
            Assert.Equal(registered.Id, snapshot.DriverId);
            Assert.True(snapshot.IsActive);
            Assert.Null(snapshot.CommittedMissionId);
            Assert.Equal([VehicleType.Van, VehicleType.HeavyTruck], snapshot.QualifiedVehicleTypes);
        }

        // Change the row behind the reader's back; a cached answer would still say Active.
        await using (var change = database.Scope())
        {
            await change.ServiceProvider.GetRequiredService<AppDbContext>().Database
                .ExecuteSqlAsync($"UPDATE drivers.drivers SET operational_status = 'Inactive' WHERE id = {registered.Id}");
        }

        await using (var again = database.Scope())
        {
            var snapshot = await again.ServiceProvider.GetRequiredService<IDriverEligibilityReader>()
                .GetAsync(registered.Id, CancellationToken.None);

            Assert.False(snapshot!.IsActive);
        }
    }

    [SkippableFact]
    public async Task An_unknown_driver_has_no_snapshot()
    {
        database.SkipWhenUnavailable();
        await using var scope = database.Scope();

        Assert.Null(await scope.ServiceProvider.GetRequiredService<IDriverEligibilityReader>()
            .GetAsync(Guid.CreateVersion7(), CancellationToken.None));
    }

    [SkippableFact]
    public async Task The_commitment_port_does_not_save_so_nothing_is_in_the_table_until_the_caller_commits()
    {
        database.SkipWhenUnavailable();
        var registered = await RegisterAndCommit(VehicleType.Truck);
        var mission = Guid.CreateVersion7();

        await using (var scope = database.Scope())
        {
            await scope.ServiceProvider.GetRequiredService<IDriverCommitments>()
                .CommitToMissionAsync(registered.Id, mission, VehicleType.Truck, CancellationToken.None);

            // The change is tracked by the caller's context and nowhere else yet.
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
        var registered = await RegisterAndCommit(VehicleType.Truck);
        var mission = Guid.CreateVersion7();

        await using (var scope = database.Scope())
        {
            await scope.ServiceProvider.GetRequiredService<IDriverCommitments>()
                .CommitToMissionAsync(registered.Id, mission, VehicleType.Truck, CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        Assert.Equal(mission, await CommittedMissionInDatabase(registered.Id));
    }

    [SkippableFact]
    public async Task A_refused_commitment_writes_nothing_at_all()
    {
        database.SkipWhenUnavailable();
        var registered = await RegisterAndCommit(VehicleType.Van);

        await using (var scope = database.Scope())
        {
            var refused = await Assert.ThrowsAsync<BusinessRuleValidationException>(() =>
                scope.ServiceProvider.GetRequiredService<IDriverCommitments>()
                    .CommitToMissionAsync(registered.Id, Guid.CreateVersion7(), VehicleType.HeavyTruck, CancellationToken.None));

            Assert.Equal("DRIVER_NOT_QUALIFIED", refused.Rule.Code);
            // Nothing was changed, so there is nothing for a rollback to undo.
            Assert.False(scope.ServiceProvider.GetRequiredService<AppDbContext>().ChangeTracker.HasChanges());
        }

        Assert.Null(await CommittedMissionInDatabase(registered.Id));
    }

    [SkippableFact]
    public async Task Releasing_through_the_port_clears_the_row_when_the_caller_commits()
    {
        database.SkipWhenUnavailable();
        var registered = await RegisterAndCommit(VehicleType.Truck);
        var mission = Guid.CreateVersion7();
        await using (var assign = database.Scope())
        {
            await assign.ServiceProvider.GetRequiredService<IDriverCommitments>()
                .CommitToMissionAsync(registered.Id, mission, VehicleType.Truck, CancellationToken.None);
            await assign.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        await using (var release = database.Scope())
        {
            await release.ServiceProvider.GetRequiredService<IDriverCommitments>()
                .ReleaseFromMissionAsync(registered.Id, mission, CancellationToken.None);
            await release.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        Assert.Null(await CommittedMissionInDatabase(registered.Id));
    }

    [SkippableFact]
    public async Task The_commitment_is_captured_in_the_entity_change_audit()
    {
        // The audit policy includes CommittedMissionId, so an assignment is accountable even though the
        // commitment is not a command of this module.
        database.SkipWhenUnavailable();
        var registered = await RegisterAndCommit(VehicleType.Truck);
        var mission = Guid.CreateVersion7();

        await using (var scope = database.Scope())
        {
            await scope.ServiceProvider.GetRequiredService<IDriverCommitments>()
                .CommitToMissionAsync(registered.Id, mission, VehicleType.Truck, CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        await using var check = database.Scope();
        var page = await check.ServiceProvider.GetRequiredService<IAuditQuery>().QueryAsync(
            new AuditQueryFilter { Module = "Drivers", EntityId = registered.Id.ToString(), Category = AuditCategory.EntityChange },
            new AuditPageRequest(1, 50),
            CancellationToken.None);

        var updated = Assert.Single(page.Items, entry => entry.Action == "Updated");
        var change = Assert.Single(updated.Changes, field => field.Name == "CommittedMissionId");
        Assert.Equal(mission.ToString(), change.After);
    }
}
