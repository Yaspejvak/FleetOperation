using Fserp.FleetOperations.Modules.Drivers.Application.Commands;
using Fserp.FleetOperations.Modules.Drivers.Application.Ports;
using Fserp.FleetOperations.Modules.Drivers.Contracts;
using Fserp.FleetOperations.Modules.Fleet.Application.Commands;
using Fserp.FleetOperations.Modules.Fleet.Application.Ports;
using Fserp.FleetOperations.Modules.Fleet.Contracts;
using Fserp.FleetOperations.Modules.Operations.Application.Commands;
using Fserp.FleetOperations.Modules.Operations.Application.Ports;
using Fserp.FleetOperations.Modules.Operations.Application.Views;
using Microsoft.Extensions.DependencyInjection;
using MPCore.Application.Time;
using MPCore.Audit;
using MPCore.Persistence.Abstractions;

namespace Fserp.FleetOperations.IntegrationTests.Operations;

/// <summary>
/// Builds the rows an Operations scenario needs — a vehicle, a driver, a mission in a given status — by
/// running the real handlers of the three modules and committing each through the unit of work, exactly
/// as the Wolverine middleware would. Nothing is inserted with raw SQL, so every row these tests read was
/// written by production code.
/// </summary>
internal static class MissionScenario
{
    public static readonly DateTimeOffset ScheduledAt = new(2026, 10, 6, 7, 30, 0, TimeSpan.Zero);

    private static string UniquePlate() => "OP-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();

    /// <summary>Registers a vehicle and commits it.</summary>
    public static async Task<Guid> RegisterVehicle(
        this PostgreSqlFixture database,
        decimal capacityKg = 5000m,
        VehicleType type = VehicleType.Truck)
    {
        await using var scope = database.Scope();
        var result = await RegisterVehicleHandler.Handle(
            new RegisterVehicle(UniquePlate(), type, capacityKg),
            scope.ServiceProvider.GetRequiredService<IVehicleRepository>(),
            scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
            scope.ServiceProvider.GetRequiredService<IClock>(),
            scope.ServiceProvider.GetRequiredService<IBusinessAuditRecorder>(),
            CancellationToken.None);
        Assert.True(result.IsSuccess);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        return result.Value.Id;
    }

    /// <summary>Registers a driver qualified for the given types and commits it.</summary>
    public static async Task<Guid> RegisterDriver(this PostgreSqlFixture database, params VehicleType[] types)
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
        return result.Value.Id;
    }

    /// <summary>Creates a mission in Draft and commits it.</summary>
    public static async Task<MissionView> CreateMission(
        this PostgreSqlFixture database,
        decimal requiredCapacityKg = 800m,
        string origin = "Tehran",
        string destination = "Isfahan")
    {
        await using var scope = database.Scope();
        var result = await CreateMissionHandler.Handle(
            new CreateMission(origin, destination, requiredCapacityKg),
            scope.ServiceProvider.GetRequiredService<IMissionRepository>(),
            scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
            scope.ServiceProvider.GetRequiredService<IClock>(),
            scope.ServiceProvider.GetRequiredService<IBusinessAuditRecorder>(),
            CancellationToken.None);
        Assert.True(result.IsSuccess);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        return result.Value;
    }

    /// <summary>Creates a mission and schedules it, each in its own committed transaction.</summary>
    public static async Task<MissionView> ScheduleMission(
        this PostgreSqlFixture database,
        decimal requiredCapacityKg = 800m)
    {
        var mission = await database.CreateMission(requiredCapacityKg);
        return await database.ScheduleExisting(mission.Id);
    }

    /// <summary>Schedules a mission that already exists, in its own committed transaction.</summary>
    public static async Task<MissionView> ScheduleExisting(this PostgreSqlFixture database, Guid missionId)
    {
        await using var scope = database.Scope();
        var result = await ScheduleMissionHandler.Handle(
            new ScheduleMission(missionId, ScheduledAt),
            scope.ServiceProvider.GetRequiredService<IMissionRepository>(),
            scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
            scope.ServiceProvider.GetRequiredService<IBusinessAuditRecorder>(),
            CancellationToken.None);
        Assert.True(result.IsSuccess);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        return result.Value;
    }

    /// <summary>
    /// Runs <c>AssignMission</c> in one scope and commits it, playing the middleware's part. The scope is
    /// the transaction boundary: one <c>AppDbContext</c> holds the mission, the vehicle and the driver.
    /// </summary>
    public static async Task<MissionView> AssignAndCommit(
        this PostgreSqlFixture database,
        Guid missionId,
        Guid vehicleId,
        Guid driverId)
    {
        await using var scope = database.Scope();
        var result = await scope.AssignAsync(missionId, vehicleId, driverId);
        Assert.True(result.IsSuccess);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        return result.Value;
    }

    /// <summary>Runs <c>AssignMission</c> in the given scope without committing it.</summary>
    public static Task<MPCore.Application.Results.Result<MissionView>> AssignAsync(
        this AsyncServiceScope scope,
        Guid missionId,
        Guid vehicleId,
        Guid driverId,
        CancellationToken cancellationToken = default) =>
        AssignMissionHandler.Handle(
            new AssignMission(missionId, vehicleId, driverId),
            scope.ServiceProvider.GetRequiredService<IMissionRepository>(),
            scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
            scope.ServiceProvider.GetRequiredService<IVehicleAvailabilityReader>(),
            scope.ServiceProvider.GetRequiredService<IDriverEligibilityReader>(),
            scope.ServiceProvider.GetRequiredService<IVehicleCommitments>(),
            scope.ServiceProvider.GetRequiredService<IDriverCommitments>(),
            scope.ServiceProvider.GetRequiredService<IBusinessAuditRecorder>(),
            cancellationToken);

    /// <summary>Runs <c>StartMission</c> in its own scope and commits it.</summary>
    public static async Task<MissionView> StartAndCommit(this PostgreSqlFixture database, Guid missionId)
    {
        await using var scope = database.Scope();
        var result = await StartMissionHandler.Handle(
            new StartMission(missionId),
            scope.ServiceProvider.GetRequiredService<IMissionRepository>(),
            scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
            scope.ServiceProvider.GetRequiredService<IBusinessAuditRecorder>(),
            CancellationToken.None);
        Assert.True(result.IsSuccess);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        return result.Value;
    }

    /// <summary>Runs <c>CompleteMission</c> in its own scope and commits it.</summary>
    public static async Task<MissionView> CompleteAndCommit(this PostgreSqlFixture database, Guid missionId)
    {
        await using var scope = database.Scope();
        var result = await CompleteMissionHandler.Handle(
            new CompleteMission(missionId),
            scope.ServiceProvider.GetRequiredService<IMissionRepository>(),
            scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
            scope.ServiceProvider.GetRequiredService<IVehicleCommitments>(),
            scope.ServiceProvider.GetRequiredService<IDriverCommitments>(),
            scope.ServiceProvider.GetRequiredService<IBusinessAuditRecorder>(),
            CancellationToken.None);
        Assert.True(result.IsSuccess);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        return result.Value;
    }

    /// <summary>Runs <c>CancelMission</c> in its own scope and commits it.</summary>
    public static async Task<MissionView> CancelAndCommit(this PostgreSqlFixture database, Guid missionId)
    {
        await using var scope = database.Scope();
        var result = await CancelMissionHandler.Handle(
            new CancelMission(missionId),
            scope.ServiceProvider.GetRequiredService<IMissionRepository>(),
            scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
            scope.ServiceProvider.GetRequiredService<IVehicleCommitments>(),
            scope.ServiceProvider.GetRequiredService<IDriverCommitments>(),
            scope.ServiceProvider.GetRequiredService<IBusinessAuditRecorder>(),
            CancellationToken.None);
        Assert.True(result.IsSuccess);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        return result.Value;
    }
}
