using Fserp.FleetOperations.Infrastructure.Persistence;
using Fserp.FleetOperations.Modules.Fleet.Application.Commands;
using Fserp.FleetOperations.Modules.Fleet.Application.Ports;
using Fserp.FleetOperations.Modules.Operations.Application;
using Fserp.FleetOperations.Modules.Operations.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MPCore.Audit;
using MPCore.Domain.Rules;
using MPCore.Persistence.Abstractions;

namespace Fserp.FleetOperations.IntegrationTests.Operations;

/// <summary>
/// The Operations audit trail against a real PostgreSQL: the action rows share the transaction of the
/// change they describe, the rejected attempts survive the rollback that refusing caused, and the entity
/// change policy captures the seven properties the plan names.
/// </summary>
/// <remarks>
/// Skipped with the fixture's reason when no PostgreSQL is reachable. "The attempt is recorded" can be
/// stated against a fake recorder; "and it is still there after the transaction rolled back" can only be
/// counted here.
/// </remarks>
[Collection(PostgreSqlCollection.Name)]
public sealed class MissionAuditTests(PostgreSqlFixture database)
{
    private async Task<IReadOnlyList<AuditEntry>> EntriesFor(Guid missionId, AuditCategory? category = null)
    {
        await using var scope = database.Scope();
        var page = await scope.ServiceProvider.GetRequiredService<IAuditQuery>().QueryAsync(
            new AuditQueryFilter
            {
                Module = OperationsAudit.Module,
                EntityId = missionId.ToString(),
                Category = category,
            },
            new AuditPageRequest(1, 100),
            CancellationToken.None);
        return page.Items;
    }

    [SkippableFact]
    public async Task Every_required_action_of_the_lifecycle_is_recorded_against_the_mission()
    {
        database.SkipWhenUnavailable();
        var vehicleId = await database.RegisterVehicle(capacityKg: 9000m);
        var driverId = await database.RegisterDriver();
        var mission = await database.ScheduleMission();
        await database.AssignAndCommit(mission.Id, vehicleId, driverId);
        await database.StartAndCommit(mission.Id);
        await database.CompleteAndCommit(mission.Id);

        var actions = (await EntriesFor(mission.Id, AuditCategory.BusinessAction))
            .Select(entry => entry.Action)
            .ToList();

        Assert.Contains(OperationsAudit.MissionCreated, actions);
        Assert.Contains(OperationsAudit.MissionScheduled, actions);
        Assert.Contains(OperationsAudit.MissionAssigned, actions);
        Assert.Contains(OperationsAudit.MissionStarted, actions);
        Assert.Contains(OperationsAudit.MissionCompleted, actions);
    }

    [SkippableFact]
    public async Task The_assignment_action_carries_the_vehicle_and_driver_ids_as_metadata()
    {
        database.SkipWhenUnavailable();
        var vehicleId = await database.RegisterVehicle(capacityKg: 9000m);
        var driverId = await database.RegisterDriver();
        var mission = await database.ScheduleMission();
        await database.AssignAndCommit(mission.Id, vehicleId, driverId);

        var assigned = Assert.Single(
            await EntriesFor(mission.Id, AuditCategory.BusinessAction),
            entry => entry.Action == OperationsAudit.MissionAssigned);

        Assert.Equal(AuditOutcome.Succeeded, assigned.Outcome);
        // The actor is the validated token's, captured by the recorder rather than passed by the handler.
        Assert.NotEqual(AuditActor.Anonymous, assigned.Actor);
        Assert.Equal(vehicleId.ToString(), assigned.Metadata![OperationsAudit.VehicleIdMetadata]);
        Assert.Equal(driverId.ToString(), assigned.Metadata[OperationsAudit.DriverIdMetadata]);
    }

    [SkippableFact]
    public async Task Assigning_a_vehicle_under_maintenance_is_refused_and_the_rejected_attempt_survives_the_rollback()
    {
        // The challenge's "a rejected operation is audited", proved end to end: the business change is
        // rolled back, so nothing of the assignment is in any of the three tables — and the attempt is
        // still in the trail, because it was written detached.
        database.SkipWhenUnavailable();
        var vehicleId = await database.RegisterVehicle(capacityKg: 9000m);
        var driverId = await database.RegisterDriver();
        var mission = await database.ScheduleMission();

        // Put the vehicle under maintenance through its own command, so the state is one production made.
        await using (var maintenance = database.Scope())
        {
            var started = await StartMaintenanceHandler.Handle(
                new StartMaintenance(vehicleId),
                maintenance.ServiceProvider.GetRequiredService<IVehicleRepository>(),
                maintenance.ServiceProvider.GetRequiredService<IUnitOfWork>(),
                maintenance.ServiceProvider.GetRequiredService<IBusinessAuditRecorder>(),
                CancellationToken.None);
            Assert.True(started.IsSuccess);
            await maintenance.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        await using (var assign = database.Scope())
        {
            var refused = await Assert.ThrowsAsync<BusinessRuleValidationException>(() =>
                assign.AssignAsync(mission.Id, vehicleId, driverId));

            Assert.Equal("VEHICLE_UNDER_MAINTENANCE", refused.Rule.Code);
            Assert.Equal("fleet", refused.Rule.ErrorDomain);
            // The scope is discarded without saving, exactly as the middleware's rollback would.
        }

        // Nothing of the refused assignment was committed.
        await using (var check = database.Scope())
        {
            var row = await check.ServiceProvider.GetRequiredService<AppDbContext>()
                .Set<Mission>()
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == mission.Id);
            Assert.Equal(MissionStatus.Scheduled, row.Status);
            Assert.Null(row.AssignedVehicleId);
            Assert.Null(row.AssignedDriverId);
        }

        // And the attempt is in the trail, under Fleet's own domain and code, against the mission.
        var attempt = Assert.Single(
            await EntriesFor(mission.Id),
            entry => entry.Outcome == AuditOutcome.Rejected);

        Assert.Equal(OperationsAudit.Module, attempt.Module);
        Assert.Equal(OperationsAudit.MissionAssigned, attempt.Action);
        Assert.Equal(OperationsAudit.MissionEntity, attempt.EntityType);
        Assert.Equal(mission.Id.ToString(), attempt.EntityId);
        Assert.Equal(new AuditFailure("fleet", "VEHICLE_UNDER_MAINTENANCE"), attempt.Failure);
        Assert.Equal(vehicleId.ToString(), attempt.Metadata![OperationsAudit.VehicleIdMetadata]);
        Assert.Equal(driverId.ToString(), attempt.Metadata[OperationsAudit.DriverIdMetadata]);
        // The actor comes from the validated token, never from the request.
        Assert.NotEqual(AuditActor.Anonymous, attempt.Actor);
    }

    [SkippableFact]
    public async Task A_refused_transition_is_recorded_as_a_rejected_attempt_too()
    {
        // The plan's "recommended also for refused transitions (MISSION_INVALID_TRANSITION)".
        database.SkipWhenUnavailable();
        var mission = await database.ScheduleMission();

        await using (var scope = database.Scope())
        {
            var refused = await Assert.ThrowsAsync<BusinessRuleValidationException>(() =>
                Fserp.FleetOperations.Modules.Operations.Application.Commands.StartMissionHandler.Handle(
                    new Fserp.FleetOperations.Modules.Operations.Application.Commands.StartMission(mission.Id),
                    scope.ServiceProvider.GetRequiredService<Fserp.FleetOperations.Modules.Operations.Application.Ports.IMissionRepository>(),
                    scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
                    scope.ServiceProvider.GetRequiredService<IBusinessAuditRecorder>(),
                    CancellationToken.None));

            Assert.Equal("MISSION_INVALID_TRANSITION", refused.Rule.Code);
        }

        var attempt = Assert.Single(
            await EntriesFor(mission.Id),
            entry => entry.Outcome == AuditOutcome.Rejected);

        Assert.Equal(OperationsAudit.MissionStarted, attempt.Action);
        Assert.Equal(new AuditFailure("operations", "MISSION_INVALID_TRANSITION"), attempt.Failure);
        Assert.Equal("Scheduled", attempt.Metadata![OperationsAudit.FromMetadata]);
        Assert.Equal("InProgress", attempt.Metadata[OperationsAudit.ToMetadata]);
    }

    [SkippableFact]
    public async Task An_unknown_vehicle_on_assign_is_recorded_as_a_rejected_attempt()
    {
        // L-22, step 2: the reader 404s are attempts on a mission that exists, so they are recorded.
        database.SkipWhenUnavailable();
        var driverId = await database.RegisterDriver();
        var mission = await database.ScheduleMission();
        var unknownVehicle = Guid.CreateVersion7();

        await using (var scope = database.Scope())
        {
            var result = await scope.AssignAsync(mission.Id, unknownVehicle, driverId);

            Assert.True(result.IsFailure);
            Assert.Equal("MISSION_VEHICLE_NOT_FOUND", result.FailureDescriptor!.Identity.Code);
        }

        var attempt = Assert.Single(
            await EntriesFor(mission.Id),
            entry => entry.Outcome == AuditOutcome.Rejected);

        Assert.Equal(new AuditFailure("operations", "MISSION_VEHICLE_NOT_FOUND"), attempt.Failure);
    }

    [SkippableFact]
    public async Task An_assignment_naming_a_mission_that_does_not_exist_records_nothing_at_all()
    {
        // L-22, step 1: there is no entity id to record it against, so the trail stays silent.
        database.SkipWhenUnavailable();
        var vehicleId = await database.RegisterVehicle(capacityKg: 9000m);
        var driverId = await database.RegisterDriver();
        var unknownMission = Guid.CreateVersion7();

        await using (var scope = database.Scope())
        {
            var result = await scope.AssignAsync(unknownMission, vehicleId, driverId);

            Assert.True(result.IsFailure);
            Assert.Equal("MISSION_NOT_FOUND", result.FailureDescriptor!.Identity.Code);
        }

        Assert.Empty(await EntriesFor(unknownMission));
    }

    [SkippableFact]
    public async Task The_entity_change_policy_captures_the_properties_the_plan_names_and_masks_none_of_them()
    {
        // docs/plans/operations.md, "Entity change policy": Status, ScheduledAt, AssignedVehicleId,
        // AssignedDriverId, RequiredCapacity, Origin, Destination — free text and ids, no personal data,
        // so nothing is masked. A masked value would be recorded as the mask rather than the value.
        database.SkipWhenUnavailable();
        var vehicleId = await database.RegisterVehicle(capacityKg: 9000m);
        var driverId = await database.RegisterDriver();
        var mission = await database.CreateMission(requiredCapacityKg: 777m, origin: "Tehran", destination: "Shiraz");

        var created = Assert.Single(
            await EntriesFor(mission.Id, AuditCategory.EntityChange),
            entry => entry.Action == "Created");
        var names = created.Changes.Select(change => change.Name).ToList();

        Assert.Contains("Status", names);
        Assert.Contains("Origin", names);
        Assert.Contains("Destination", names);
        Assert.Contains("RequiredCapacity", names);
        Assert.Contains("ScheduledAt", names);
        Assert.Contains("AssignedVehicleId", names);
        Assert.Contains("AssignedDriverId", names);
        // Recorded verbatim: a location is a place, not a person.
        Assert.Equal("Tehran", Assert.Single(created.Changes, change => change.Name == "Origin").After);
        Assert.Equal("Shiraz", Assert.Single(created.Changes, change => change.Name == "Destination").After);
        Assert.Equal("777", Assert.Single(created.Changes, change => change.Name == "RequiredCapacity").After);
        Assert.Equal("Draft", Assert.Single(created.Changes, change => change.Name == "Status").After);

        // And a later assignment is captured as a change of the two id columns and the status.
        await database.ScheduleExisting(mission.Id);
        await database.AssignAndCommit(mission.Id, vehicleId, driverId);

        var updated = (await EntriesFor(mission.Id, AuditCategory.EntityChange))
            .Where(entry => entry.Action == "Updated")
            .SelectMany(entry => entry.Changes)
            .ToList();

        Assert.Equal(
            vehicleId.ToString(),
            Assert.Single(updated, change => change.Name == "AssignedVehicleId" && change.After == vehicleId.ToString()).After);
        Assert.Contains(updated, change => change.Name == "Status" && change.After == "Assigned");
    }
}
