using Fserp.FleetOperations.Modules.Fleet.Application;
using Fserp.FleetOperations.Modules.Fleet.Application.Events;
using Fserp.FleetOperations.Modules.Fleet.Application.Views;
using Fserp.FleetOperations.Modules.Fleet.Domain;
using Fserp.FleetOperations.Modules.Fleet.Domain.Events;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fserp.FleetOperations.IntegrationTests.Fleet;

/// <summary>
/// Decision 4's first guarantee, against a real Redis and the real <c>VehicleReadModel</c> over real
/// PostgreSQL: after each of the six eviction triggers, a read of the available list returns the fresh
/// list and not the pre-trigger one.
/// </summary>
/// <remarks>
/// <para>
/// Every case has the same four steps, and the third is what keeps it from passing vacuously:
/// </para>
/// <list type="number">
/// <item>read once, which fills the in-process level and Redis with the pre-trigger list;</item>
/// <item>change the vehicle through its real production path — a command handler, or the
/// <c>IVehicleCommitments</c> port Operations calls — and commit it;</item>
/// <item>read again <em>without</em> evicting, and require the answer to still be the pre-trigger one.
/// A cache that was not really caching would already answer freshly here, and the case would prove
/// nothing; this step fails if that is so;</item>
/// <item>hand the domain event to the real <c>AvailableVehiclesCacheEvictionHandler</c>, read a third
/// time, and require the fresh list — compared both against the changed vehicle and, as a set, against
/// what PostgreSQL answers with no cache in the way.</item>
/// </list>
/// <para>
/// The handler is invoked directly rather than published, because Wolverine's routing of all six events
/// to it is already proved by
/// <c>Fserp.FleetOperations.UnitTests.Fleet.CacheEvictionRoutingTests</c>. What is unproved there, and
/// proved here, is that the removal reaches Redis and that the next read is therefore fresh.
/// </para>
/// <para>
/// The handler's logger is <see cref="NullLogger"/>: the warning it writes when a removal fails belongs to
/// the outage path, which <c>Fserp.FleetOperations.UnitTests.Fleet.CacheEvictionOutageTests</c> proves
/// without containers. Here Redis is up, and a removal that nevertheless failed is still caught by step 4 —
/// the third read would answer the pre-trigger list and the case would fail.
/// </para>
/// </remarks>
/// <param name="database">The PostgreSQL fixture.</param>
/// <param name="redis">The Redis fixture.</param>
[Collection(FleetCacheCollection.Name)]
[Trait(CacheGate.TraitName, CacheGate.TraitValue)]
public sealed class AvailableVehiclesCacheEvictionTests(PostgreSqlFixture database, RedisFixture redis) : IAsyncLifetime
{
    private readonly FleetCacheScenario _scenario = new(database);
    private AvailableVehiclesCacheHost? _cache;

    public Task InitializeAsync()
    {
        // Composed only when both dependencies are there; otherwise every test in the class skips.
        if (CacheGate.UnavailableReason(database, redis) is null)
        {
            _cache = new AvailableVehiclesCacheHost(redis.RequireConnectionString());
        }

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_cache is not null)
        {
            await _cache.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task VehicleRegistered_evicts_the_list_so_the_new_vehicle_appears()
    {
        CacheGate.SkipWhenUnavailable(database, redis);
        var cache = await ColdCacheAsync();

        var before = await ReadAsync();
        var vehicle = await _scenario.RegisterAsync();
        Assert.DoesNotContain(vehicle.Id, Ids(before));

        Assert.DoesNotContain(vehicle.Id, Ids(await ReadAsync()));

        await AvailableVehiclesCacheEvictionHandler.Handle(
            new VehicleRegistered(vehicle.Id), cache.Cache, NullLogger.Instance, CancellationToken.None);

        var after = await ReadAsync();
        Assert.Contains(vehicle.Id, Ids(after));
        await AssertIsTheFreshListAsync(after);
    }

    [SkippableFact]
    public async Task VehicleStatusChanged_evicts_the_list_so_the_deactivated_vehicle_disappears()
    {
        CacheGate.SkipWhenUnavailable(database, redis);
        var cache = await ColdCacheAsync();
        var vehicle = await _scenario.RegisterAsync();

        var before = await ReadAsync();
        Assert.Contains(vehicle.Id, Ids(before));
        await _scenario.ChangeStatusAsync(vehicle.Id, OperationalStatus.Inactive);

        Assert.Contains(vehicle.Id, Ids(await ReadAsync()));

        await AvailableVehiclesCacheEvictionHandler.Handle(
            new VehicleStatusChanged(vehicle.Id, OperationalStatus.Active, OperationalStatus.Inactive),
            cache.Cache,
            NullLogger.Instance,
            CancellationToken.None);

        var after = await ReadAsync();
        Assert.DoesNotContain(vehicle.Id, Ids(after));
        await AssertIsTheFreshListAsync(after);
    }

    [SkippableFact]
    public async Task MaintenanceStarted_evicts_the_list_so_the_vehicle_under_maintenance_disappears()
    {
        CacheGate.SkipWhenUnavailable(database, redis);
        var cache = await ColdCacheAsync();
        var vehicle = await _scenario.RegisterAsync();

        var before = await ReadAsync();
        Assert.Contains(vehicle.Id, Ids(before));
        await _scenario.StartMaintenanceAsync(vehicle.Id);

        Assert.Contains(vehicle.Id, Ids(await ReadAsync()));

        await AvailableVehiclesCacheEvictionHandler.Handle(
            new MaintenanceStarted(vehicle.Id), cache.Cache, NullLogger.Instance, CancellationToken.None);

        var after = await ReadAsync();
        Assert.DoesNotContain(vehicle.Id, Ids(after));
        await AssertIsTheFreshListAsync(after);
    }

    [SkippableFact]
    public async Task MaintenanceCompleted_evicts_the_list_so_the_repaired_vehicle_reappears()
    {
        CacheGate.SkipWhenUnavailable(database, redis);
        var cache = await ColdCacheAsync();
        var vehicle = await _scenario.RegisterAsync();
        await _scenario.StartMaintenanceAsync(vehicle.Id);

        var before = await ReadAsync();
        Assert.DoesNotContain(vehicle.Id, Ids(before));
        await _scenario.CompleteMaintenanceAsync(vehicle.Id);

        Assert.DoesNotContain(vehicle.Id, Ids(await ReadAsync()));

        await AvailableVehiclesCacheEvictionHandler.Handle(
            new MaintenanceCompleted(vehicle.Id), cache.Cache, NullLogger.Instance, CancellationToken.None);

        var after = await ReadAsync();
        Assert.Contains(vehicle.Id, Ids(after));
        await AssertIsTheFreshListAsync(after);
    }

    [SkippableFact]
    public async Task VehicleCommittedToMission_evicts_the_list_so_the_committed_vehicle_disappears()
    {
        CacheGate.SkipWhenUnavailable(database, redis);
        var cache = await ColdCacheAsync();
        var vehicle = await _scenario.RegisterAsync();
        var mission = Guid.CreateVersion7();

        var before = await ReadAsync();
        Assert.Contains(vehicle.Id, Ids(before));
        await _scenario.CommitToMissionAsync(vehicle.Id, mission);

        Assert.Contains(vehicle.Id, Ids(await ReadAsync()));

        await AvailableVehiclesCacheEvictionHandler.Handle(
            new VehicleCommittedToMission(vehicle.Id, mission),
            cache.Cache,
            NullLogger.Instance,
            CancellationToken.None);

        var after = await ReadAsync();
        Assert.DoesNotContain(vehicle.Id, Ids(after));
        await AssertIsTheFreshListAsync(after);
    }

    [SkippableFact]
    public async Task VehicleReleasedFromMission_evicts_the_list_so_the_released_vehicle_reappears()
    {
        CacheGate.SkipWhenUnavailable(database, redis);
        var cache = await ColdCacheAsync();
        var vehicle = await _scenario.RegisterAsync();
        var mission = Guid.CreateVersion7();
        await _scenario.CommitToMissionAsync(vehicle.Id, mission);

        var before = await ReadAsync();
        Assert.DoesNotContain(vehicle.Id, Ids(before));
        await _scenario.ReleaseFromMissionAsync(vehicle.Id, mission);

        Assert.DoesNotContain(vehicle.Id, Ids(await ReadAsync()));

        await AvailableVehiclesCacheEvictionHandler.Handle(
            new VehicleReleasedFromMission(vehicle.Id, mission),
            cache.Cache,
            NullLogger.Instance,
            CancellationToken.None);

        var after = await ReadAsync();
        Assert.Contains(vehicle.Id, Ids(after));
        await AssertIsTheFreshListAsync(after);
    }

    /// <summary>The composed cache with the one decision 4 key removed, so each case starts from a miss.</summary>
    private async Task<AvailableVehiclesCacheHost> ColdCacheAsync()
    {
        var cache = _cache ?? throw new InvalidOperationException("The cache was not composed.");
        await cache.Cache.RemoveAsync(FleetCacheKeys.AvailableVehicles, CancellationToken.None);
        return cache;
    }

    private Task<IReadOnlyList<AvailableVehicleView>> ReadAsync()
    {
        var cache = _cache ?? throw new InvalidOperationException("The cache was not composed.");
        return _scenario.ReadThroughCacheAsync(cache.ReadThrough, cache.Options);
    }

    /// <summary>The cached answer is the one PostgreSQL gives with no cache in the way.</summary>
    private async Task AssertIsTheFreshListAsync(IReadOnlyList<AvailableVehicleView> cached) =>
        Assert.Equal(Ids(await _scenario.ReadFromDatabaseAsync()), Ids(cached));

    /// <summary>The identities in the order the read model produces them, so two lists compare exactly.</summary>
    private static IReadOnlyList<Guid> Ids(IEnumerable<AvailableVehicleView> list) =>
        list.Select(static row => row.Id).Order().ToList();
}
