using Fserp.FleetOperations.Api.Hosting;
using Fserp.FleetOperations.Infrastructure.Persistence;
using Fserp.FleetOperations.Modules.Fleet.Application.Commands;
using Fserp.FleetOperations.Modules.Fleet.Application.Ports;
using Fserp.FleetOperations.Modules.Fleet.Contracts;
using Fserp.FleetOperations.Modules.Operations.Domain;
using Fserp.FleetOperations.Modules.Operations.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MPCore.Application.Results;
using MPCore.Audit;
using MPCore.Domain.Rules;
using MPCore.Persistence.Abstractions;
using Npgsql;

namespace Fserp.FleetOperations.IntegrationTests.Operations;

/// <summary>
/// Decision 2 proved against a real PostgreSQL: when several assignments race for the same vehicle — or
/// the same driver — <b>exactly one commits</b>, and every loser is refused by the <c>xmin</c>
/// concurrency token or by one of the two unique partial indexes, both of which this host maps to
/// <c>409</c>.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here can be proved with an in-memory provider: <c>xmin</c> is a PostgreSQL system column and a
/// partial index is a PostgreSQL feature. Each test therefore skips with the fixture's reason when no
/// database is reachable, and the guarantee is then <b>unverified</b> rather than assumed.
/// </para>
/// <para>
/// Each racer runs in its own scope, and therefore its own <c>AppDbContext</c>, its own connection and its
/// own transaction — the same shape the Wolverine middleware gives one request. They are released
/// together by a <see cref="Barrier"/>, so the reads genuinely interleave before any commit; the test
/// plays the middleware's part by calling <c>SaveChangesAsync</c> itself.
/// </para>
/// <para>
/// The outcomes a loser may have are enumerated, not summarised: a broken rule before the commit
/// (<c>422 VEHICLE_NOT_AVAILABLE</c> / <c>DRIVER_NOT_AVAILABLE</c>, because the winner's row was already
/// visible), a lost <c>xmin</c> check at the commit (<c>409</c>), or a unique violation on one of the two
/// partial indexes (<c>409</c>). Anything else fails the test.
/// </para>
/// </remarks>
[Collection(PostgreSqlCollection.Name)]
public sealed class ConcurrentAssignmentTests(PostgreSqlFixture database)
{
    private const int Racers = 8;

    /// <summary>How one racer ended, classified by the failure a caller would actually receive.</summary>
    private enum RaceOutcome
    {
        /// <summary>The assignment committed.</summary>
        Committed,

        /// <summary>A rule refused it before the commit: the winner was already visible. Maps to 422.</summary>
        RefusedByRule,

        /// <summary>The xmin check matched no row at commit. Maps to 409.</summary>
        LostConcurrencyToken,

        /// <summary>A unique partial index refused the mission row at commit. Maps to 409.</summary>
        RefusedByUniqueIndex,
    }

    private sealed record RaceResult(RaceOutcome Outcome, string? Code, string? Index, Guid MissionId);

    /// <summary>
    /// Runs <paramref name="count"/> assignments at once, each in its own scope, transaction and
    /// connection, released together.
    /// </summary>
    private async Task<IReadOnlyList<RaceResult>> Race(
        IReadOnlyList<Guid> missionIds,
        Func<int, Guid> vehicleOf,
        Func<int, Guid> driverOf,
        int count)
    {
        using var barrier = new Barrier(count);
        var racers = Enumerable.Range(0, count)
            .Select(index => Task.Run(async () =>
            {
                await using var scope = database.Scope();
                var missionId = missionIds[index];

                // Every racer reaches this line before any of them proceeds, so the loads and the snapshot
                // reads interleave and each racer sees the vehicle as free.
                barrier.SignalAndWait();

                try
                {
                    var result = await scope.AssignAsync(missionId, vehicleOf(index), driverOf(index));
                    if (result.IsFailure)
                    {
                        return new RaceResult(RaceOutcome.RefusedByRule, result.FailureDescriptor!.Identity.Code, null, missionId);
                    }

                    // The middleware's commit, played by the test.
                    await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
                    return new RaceResult(RaceOutcome.Committed, null, null, missionId);
                }
                catch (BusinessRuleValidationException refused)
                {
                    return new RaceResult(RaceOutcome.RefusedByRule, refused.Rule.Code, null, missionId);
                }
                catch (DbUpdateConcurrencyException)
                {
                    return new RaceResult(RaceOutcome.LostConcurrencyToken, null, null, missionId);
                }
                catch (DbUpdateException failed) when (UniqueViolationIndexOf(failed) is { } index)
                {
                    return new RaceResult(RaceOutcome.RefusedByUniqueIndex, null, index, missionId);
                }
            }))
            .ToList();

        return await Task.WhenAll(racers);
    }

    private static string? UniqueViolationIndexOf(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: { } index })
            {
                return index;
            }
        }

        return null;
    }

    private async Task<(MissionStatus Status, Guid? VehicleId, Guid? DriverId)> MissionRow(Guid missionId)
    {
        await using var scope = database.Scope();
        var mission = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .Set<Mission>()
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == missionId);
        return (mission.Status, mission.AssignedVehicleId, mission.AssignedDriverId);
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

    private async Task<int> ActiveMissionsHolding(string column, Guid resourceId)
    {
        await using var scope = database.Scope();
        var sql = $"SELECT count(*)::int AS \"Value\" FROM operations.missions "
            + $"WHERE {column} = @resource AND status IN ('Assigned', 'InProgress')";
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database
            .SqlQueryRaw<int>(sql, new NpgsqlParameter("resource", resourceId))
            .SingleAsync();
    }

    /// <summary>Every loser's outcome is one of the three the design predicts, and none is a surprise.</summary>
    private static void AssertEveryLoserIsAccountedFor(IReadOnlyList<RaceResult> results, params string[] expectedRuleCodes)
    {
        foreach (var loser in results.Where(result => result.Outcome != RaceOutcome.Committed))
        {
            switch (loser.Outcome)
            {
                case RaceOutcome.RefusedByRule:
                    // 422 under the owning module's own code, because the winner's write was already visible.
                    Assert.Contains(loser.Code, expectedRuleCodes);
                    break;
                case RaceOutcome.LostConcurrencyToken:
                    // 409 fleetoperations/CONCURRENCY_CONFLICT.
                    Assert.Equal(
                        "CONCURRENCY_CONFLICT",
                        ConcurrencyExceptionMapper.TryMap(new DbUpdateConcurrencyException())!.Identity.Code);
                    break;
                case RaceOutcome.RefusedByUniqueIndex:
                    // The index that refused it must be one this host maps, or the caller would see a 500.
                    Assert.Contains(
                        loser.Index,
                        new[] { MissionConfiguration.ActiveVehicleUniqueIndex, MissionConfiguration.ActiveDriverUniqueIndex });
                    break;
                default:
                    Assert.Fail($"Unexpected outcome {loser.Outcome} for mission {loser.MissionId}.");
                    break;
            }
        }
    }

    [SkippableFact]
    public async Task Eight_missions_assigning_the_same_vehicle_at_once_commit_exactly_one()
    {
        database.SkipWhenUnavailable();
        var vehicleId = await database.RegisterVehicle(capacityKg: 9000m);
        // One driver each, so the only contested resource is the vehicle.
        var driverIds = await Task.WhenAll(Enumerable.Range(0, Racers).Select(_ => database.RegisterDriver()));
        var missions = await Task.WhenAll(Enumerable.Range(0, Racers).Select(_ => database.ScheduleMission()));
        var missionIds = missions.Select(mission => mission.Id).ToList();

        var results = await Race(missionIds, _ => vehicleId, index => driverIds[index], Racers);

        // Exactly one commits. This is the guarantee; everything below explains how the others were stopped.
        Assert.Single(results, result => result.Outcome == RaceOutcome.Committed);
        AssertEveryLoserIsAccountedFor(results, "VEHICLE_NOT_AVAILABLE");

        // The database agrees with the winner, from both sides of the relationship.
        var winner = results.Single(result => result.Outcome == RaceOutcome.Committed);
        Assert.Equal(winner.MissionId, await CommittedMissionOfVehicle(vehicleId));
        Assert.Equal(1, await ActiveMissionsHolding("assigned_vehicle_id", vehicleId));
        var row = await MissionRow(winner.MissionId);
        Assert.Equal(MissionStatus.Assigned, row.Status);
        Assert.Equal(vehicleId, row.VehicleId);

        // Every loser changed nothing at all: no half-assigned mission is left behind.
        foreach (var loser in results.Where(result => result.Outcome != RaceOutcome.Committed))
        {
            var losing = await MissionRow(loser.MissionId);
            Assert.Equal(MissionStatus.Scheduled, losing.Status);
            Assert.Null(losing.VehicleId);
            Assert.Null(losing.DriverId);
        }
    }

    [SkippableFact]
    public async Task Eight_missions_assigning_the_same_driver_at_once_commit_exactly_one()
    {
        database.SkipWhenUnavailable();
        var driverId = await database.RegisterDriver(VehicleType.Truck);
        // One vehicle each, so the only contested resource is the driver.
        var vehicleIds = await Task.WhenAll(
            Enumerable.Range(0, Racers).Select(_ => database.RegisterVehicle(capacityKg: 9000m)));
        var missions = await Task.WhenAll(Enumerable.Range(0, Racers).Select(_ => database.ScheduleMission()));
        var missionIds = missions.Select(mission => mission.Id).ToList();

        var results = await Race(missionIds, index => vehicleIds[index], _ => driverId, Racers);

        Assert.Single(results, result => result.Outcome == RaceOutcome.Committed);
        AssertEveryLoserIsAccountedFor(results, "DRIVER_NOT_AVAILABLE");

        var winner = results.Single(result => result.Outcome == RaceOutcome.Committed);
        Assert.Equal(winner.MissionId, await CommittedMissionOfDriver(driverId));
        Assert.Equal(1, await ActiveMissionsHolding("assigned_driver_id", driverId));
        Assert.Equal(driverId, (await MissionRow(winner.MissionId)).DriverId);

        foreach (var loser in results.Where(result => result.Outcome != RaceOutcome.Committed))
        {
            Assert.Equal(MissionStatus.Scheduled, (await MissionRow(loser.MissionId)).Status);
        }
    }

    [SkippableFact]
    public async Task Two_missions_contesting_both_the_vehicle_and_the_driver_commit_exactly_one()
    {
        // The narrowest race, and the one whose interleaving is easiest to reason about: two requests,
        // identical in every input but the mission.
        database.SkipWhenUnavailable();
        var vehicleId = await database.RegisterVehicle(capacityKg: 9000m);
        var driverId = await database.RegisterDriver();
        var first = await database.ScheduleMission();
        var second = await database.ScheduleMission();

        var results = await Race([first.Id, second.Id], _ => vehicleId, _ => driverId, 2);

        Assert.Single(results, result => result.Outcome == RaceOutcome.Committed);
        AssertEveryLoserIsAccountedFor(results, "VEHICLE_NOT_AVAILABLE", "DRIVER_NOT_AVAILABLE");
        Assert.Equal(1, await ActiveMissionsHolding("assigned_vehicle_id", vehicleId));
        Assert.Equal(1, await ActiveMissionsHolding("assigned_driver_id", driverId));
    }

    [SkippableFact]
    public async Task An_assignment_racing_a_start_of_maintenance_does_not_leave_a_vehicle_under_maintenance_on_a_mission()
    {
        // Decision 3's race: both commands write the vehicle row, so the vehicle's xmin catches whichever
        // of them reads it first and commits second. Whatever the order, the two outcomes that would be
        // wrong — an assigned mission holding a vehicle under maintenance, or a vehicle under maintenance
        // with a mission commitment — cannot both be true at the end.
        database.SkipWhenUnavailable();
        var vehicleId = await database.RegisterVehicle(capacityKg: 9000m);
        var driverId = await database.RegisterDriver();
        var mission = await database.ScheduleMission();

        using var barrier = new Barrier(2);
        var assign = Task.Run(async () =>
        {
            await using var scope = database.Scope();
            barrier.SignalAndWait();
            try
            {
                var result = await scope.AssignAsync(mission.Id, vehicleId, driverId);
                if (result.IsFailure)
                {
                    return false;
                }

                await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
                return true;
            }
            catch (Exception exception) when (
                exception is BusinessRuleValidationException or DbUpdateConcurrencyException
                || UniqueViolationIndexOf(exception) is not null)
            {
                return false;
            }
        });
        var maintain = Task.Run(async () =>
        {
            await using var scope = database.Scope();
            barrier.SignalAndWait();
            try
            {
                var result = await StartMaintenanceHandler.Handle(
                    new StartMaintenance(vehicleId),
                    scope.ServiceProvider.GetRequiredService<IVehicleRepository>(),
                    scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
                    scope.ServiceProvider.GetRequiredService<IBusinessAuditRecorder>(),
                    CancellationToken.None);
                if (result.IsFailure)
                {
                    return false;
                }

                await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
                return true;
            }
            catch (Exception exception) when (
                exception is BusinessRuleValidationException or DbUpdateConcurrencyException)
            {
                return false;
            }
        });

        var assigned = await assign;
        var maintained = await maintain;

        // At most one of the two may have committed; both committing is the write skew decision 2 exists
        // to prevent.
        Assert.False(assigned && maintained, "Assign and Start Maintenance both committed against one vehicle.");
        await using var check = database.Scope();
        var underMaintenance = await check.ServiceProvider.GetRequiredService<AppDbContext>().Database
            .SqlQuery<string>($"SELECT maintenance_status AS \"Value\" FROM fleet.vehicles WHERE id = {vehicleId}")
            .SingleAsync();
        var commitment = await CommittedMissionOfVehicle(vehicleId);
        Assert.False(
            underMaintenance == "UnderMaintenance" && commitment is not null,
            "A vehicle is under maintenance and committed to a mission at the same time.");
    }

    [SkippableFact]
    public async Task A_request_arriving_after_the_winner_committed_is_refused_as_a_rule_and_not_as_a_race()
    {
        // The non-concurrent companion of the tests above: once the winner is visible, the refusal is a
        // plain 422 from the Vehicle aggregate, not a 409. A caller can tell "you lost a race" from
        // "that vehicle is taken".
        database.SkipWhenUnavailable();
        var vehicleId = await database.RegisterVehicle(capacityKg: 9000m);
        var firstDriver = await database.RegisterDriver();
        var secondDriver = await database.RegisterDriver();
        var first = await database.ScheduleMission();
        var second = await database.ScheduleMission();
        await database.AssignAndCommit(first.Id, vehicleId, firstDriver);

        await using var scope = database.Scope();
        var refused = await Assert.ThrowsAsync<BusinessRuleValidationException>(() =>
            scope.AssignAsync(second.Id, vehicleId, secondDriver));

        Assert.Equal("VEHICLE_NOT_AVAILABLE", refused.Rule.Code);
        Assert.Equal("fleet", refused.Rule.ErrorDomain);
        Assert.Equal(MissionStatus.Scheduled, (await MissionRow(second.Id)).Status);
    }

    [SkippableFact]
    public async Task The_unique_partial_index_refuses_a_second_active_mission_even_when_no_resource_row_is_written()
    {
        // The set-based half of decision 2, isolated: "a vehicle never holds two active missions, even if
        // a code path bypasses Fleet". The mission row is written directly, so the vehicle's xmin cannot
        // catch it and only the index can. Raw SQL is deliberate here — it is the bypass being tested.
        database.SkipWhenUnavailable();
        var vehicleId = await database.RegisterVehicle(capacityKg: 9000m);
        var driverId = await database.RegisterDriver();
        var first = await database.ScheduleMission();
        var second = await database.ScheduleMission();
        await database.AssignAndCommit(first.Id, vehicleId, driverId);

        await using var scope = database.Scope();
        var database_ = scope.ServiceProvider.GetRequiredService<AppDbContext>().Database;
        var refused = await Assert.ThrowsAsync<PostgresException>(() => database_.ExecuteSqlAsync(
            $"UPDATE operations.missions SET status = 'Assigned', assigned_vehicle_id = {vehicleId} WHERE id = {second.Id}"));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, refused.SqlState);
        Assert.Equal(MissionConfiguration.ActiveVehicleUniqueIndex, refused.ConstraintName);
        // And this host maps that violation to the 409 a caller sees, rather than letting it be a 500.
        Assert.Equal("CONCURRENCY_CONFLICT", UniqueViolations.TryMap(refused)!.Identity.Code);
        Assert.Equal(ErrorCategory.Concurrency, UniqueViolations.TryMap(refused)!.Category);
    }

    [SkippableFact]
    public async Task The_partial_index_lets_a_completed_mission_keep_its_ids_without_blocking_the_next_assignment()
    {
        // The "partial" in "unique partial index": the predicate is Assigned and InProgress (O-8), so a
        // completed mission keeps the record of who carried it and the vehicle is free again.
        database.SkipWhenUnavailable();
        var vehicleId = await database.RegisterVehicle(capacityKg: 9000m);
        var driverId = await database.RegisterDriver();
        var first = await database.ScheduleMission();
        var second = await database.ScheduleMission();

        await database.AssignAndCommit(first.Id, vehicleId, driverId);
        await database.StartAndCommit(first.Id);
        await database.CompleteAndCommit(first.Id);

        // The first mission still names both resources, and the next assignment is accepted.
        var completed = await MissionRow(first.Id);
        Assert.Equal(MissionStatus.Completed, completed.Status);
        Assert.Equal(vehicleId, completed.VehicleId);
        Assert.Equal(driverId, completed.DriverId);

        await database.AssignAndCommit(second.Id, vehicleId, driverId);

        Assert.Equal(1, await ActiveMissionsHolding("assigned_vehicle_id", vehicleId));
        Assert.Equal(second.Id, await CommittedMissionOfVehicle(vehicleId));
    }
}
