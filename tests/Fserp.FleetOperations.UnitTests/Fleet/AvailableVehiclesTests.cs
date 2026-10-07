using System.Reflection;
using System.Text.Json;
using Fserp.FleetOperations.Modules.Fleet.Application;
using Fserp.FleetOperations.Modules.Fleet.Application.Events;
using Fserp.FleetOperations.Modules.Fleet.Application.Ports;
using Fserp.FleetOperations.Modules.Fleet.Application.Queries;
using Fserp.FleetOperations.Modules.Fleet.Application.Views;
using Fserp.FleetOperations.Modules.Fleet.Contracts;
using Fserp.FleetOperations.Modules.Fleet.Domain;
using Fserp.FleetOperations.Modules.Fleet.Domain.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MPCore.Caching.Abstractions;
using MPCore.Domain.Events;

namespace Fserp.FleetOperations.UnitTests.Fleet;

/// <summary>
/// The one definition of "available" (docs/plans/fleet.md): the read model translates this expression to
/// SQL, so evaluating it in memory tests the same predicate the database runs, not a copy of it.
/// </summary>
public sealed class VehicleAvailabilityTests
{
    [Fact]
    public void A_newly_registered_vehicle_is_available()
    {
        Assert.True(VehicleAvailability.IsAvailable(TestVehicles.Registered()));
    }

    [Fact]
    public void An_Inactive_vehicle_is_not_available()
    {
        var vehicle = TestVehicles.Registered();
        vehicle.ChangeStatus(OperationalStatus.Inactive);

        Assert.False(VehicleAvailability.IsAvailable(vehicle));
    }

    [Fact]
    public void A_vehicle_under_maintenance_is_not_available()
    {
        var vehicle = TestVehicles.Registered();
        vehicle.StartMaintenance();

        Assert.False(VehicleAvailability.IsAvailable(vehicle));
    }

    [Fact]
    public void A_committed_vehicle_is_not_available()
    {
        var vehicle = TestVehicles.Registered().CommittedTo(Guid.CreateVersion7());

        Assert.False(VehicleAvailability.IsAvailable(vehicle));
    }

    [Fact]
    public void Leaving_maintenance_makes_an_Active_uncommitted_vehicle_available_again()
    {
        var vehicle = TestVehicles.Registered();
        vehicle.StartMaintenance();
        vehicle.CompleteMaintenance();

        Assert.True(VehicleAvailability.IsAvailable(vehicle));
    }

    [Fact]
    public void An_Inactive_vehicle_out_of_maintenance_is_still_not_available()
    {
        // The three conditions are conjunctive: fixing one does not make the vehicle available.
        var vehicle = TestVehicles.Registered();
        vehicle.ChangeStatus(OperationalStatus.Inactive);
        vehicle.StartMaintenance();
        vehicle.CompleteMaintenance();

        Assert.False(VehicleAvailability.IsAvailable(vehicle));
    }

    [Fact]
    public void The_specification_names_the_three_conditions_the_plan_names()
    {
        // The read model translates this expression; its text pins what the SQL will ask for.
        var text = VehicleAvailability.Specification.ToString();

        Assert.Contains(nameof(Vehicle.OperationalStatus), text, StringComparison.Ordinal);
        Assert.Contains(nameof(Vehicle.MaintenanceStatus), text, StringComparison.Ordinal);
        Assert.Contains(nameof(Vehicle.CommittedMissionId), text, StringComparison.Ordinal);
    }
}

/// <summary>A read-through cache that records what it was asked and whether the factory had to run.</summary>
internal sealed class FakeReadThroughCache(object? stored = null) : IReadThroughCache
{
    private object? _stored = stored;

    public List<string> Keys { get; } = [];

    public List<TimeSpan?> Expirations { get; } = [];

    public int FactoryRuns { get; private set; }

    public async ValueTask<T> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, ValueTask<T>> factory,
        TimeSpan? absoluteExpiration = null,
        CancellationToken cancellationToken = default)
    {
        Keys.Add(key);
        Expirations.Add(absoluteExpiration);
        if (_stored is T hit)
        {
            return hit;
        }

        FactoryRuns++;
        var created = await factory(cancellationToken);
        _stored = created;
        return created;
    }
}

/// <summary>An <see cref="ICache"/> that records every removal.</summary>
internal sealed class FakeCache : ICache
{
    public List<string> Removed { get; } = [];

    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult<T?>(default);

    public Task SetAsync<T>(string key, T value, TimeSpan? absoluteExpiration = null, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        Removed.Add(key);
        return Task.CompletedTask;
    }
}

internal sealed class FakeAvailableReadModel(params AvailableVehicleView[] available) : IVehicleReadModel
{
    public int Reads { get; private set; }

    public Task<VehicleView?> GetAsync(Guid vehicleId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This fake serves the available list only.");

    public Task<IReadOnlyList<AvailableVehicleView>> GetAvailableAsync(CancellationToken cancellationToken)
    {
        Reads++;
        return Task.FromResult<IReadOnlyList<AvailableVehicleView>>(available);
    }

    // Added when the port gained the Contracts snapshot (round 5). This fake serves the available list
    // only; the snapshot has its own fake and its own tests. Throwing keeps a mistaken call visible.
    public Task<VehicleSnapshot?> GetSnapshotAsync(Guid vehicleId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This fake serves the available list only.");
}

/// <summary>
/// <c>GetAvailableVehiclesHandler</c> (docs/architecture.md, decision 4): it reads through the cache with
/// the shared key and the 30 s bound from module options, and touches the database only on a miss.
/// </summary>
public sealed class GetAvailableVehiclesHandlerTests
{
    private static readonly AvailableVehicleView One =
        new(Guid.CreateVersion7(), "AB-1", VehicleType.Truck, 1200m);

    private static IOptions<FleetCacheOptions> Options(TimeSpan? expiration = null) =>
        Microsoft.Extensions.Options.Options.Create(
            expiration is null ? new FleetCacheOptions() : new FleetCacheOptions { AvailableVehiclesExpiration = expiration.Value });

    [Fact]
    public async Task A_miss_serves_the_factory_which_reads_the_database_once()
    {
        var cache = new FakeReadThroughCache();
        var readModel = new FakeAvailableReadModel(One);

        var result = await GetAvailableVehiclesHandler.Handle(
            new GetAvailableVehicles(), cache, readModel, Options(), CancellationToken.None);

        Assert.Equal([One], result);
        Assert.Equal(1, cache.FactoryRuns);
        Assert.Equal(1, readModel.Reads);
    }

    [Fact]
    public async Task A_hit_answers_from_the_cache_without_touching_the_database()
    {
        var cache = new FakeReadThroughCache(new[] { One });
        var readModel = new FakeAvailableReadModel(One);

        var result = await GetAvailableVehiclesHandler.Handle(
            new GetAvailableVehicles(), cache, readModel, Options(), CancellationToken.None);

        Assert.Equal([One], result);
        Assert.Equal(0, cache.FactoryRuns);
        Assert.Equal(0, readModel.Reads);
    }

    [Fact]
    public async Task The_read_uses_the_module_key_constant_bare()
    {
        var cache = new FakeReadThroughCache();

        await GetAvailableVehiclesHandler.Handle(
            new GetAvailableVehicles(), cache, new FakeAvailableReadModel(), Options(), CancellationToken.None);

        // Bare: MPCoreCacheOptions.KeyPrefix is applied by the adapter, so prepending it here would
        // qualify the key twice and the eviction would miss the entry the read created.
        Assert.Equal(FleetCacheKeys.AvailableVehicles, Assert.Single(cache.Keys));
        Assert.Equal("fleet:vehicles:available:v1", FleetCacheKeys.AvailableVehicles);
    }

    [Fact]
    public async Task The_expiration_is_the_module_option_whose_default_is_thirty_seconds()
    {
        // F-6: up to 30 s of staleness is accepted, and the bound comes from module options.
        Assert.Equal(TimeSpan.FromSeconds(30), new FleetCacheOptions().AvailableVehiclesExpiration);
        var cache = new FakeReadThroughCache();

        await GetAvailableVehiclesHandler.Handle(
            new GetAvailableVehicles(), cache, new FakeAvailableReadModel(), Options(), CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(30), Assert.Single(cache.Expirations));
    }

    [Fact]
    public async Task A_configured_expiration_is_the_one_passed_to_the_cache()
    {
        var cache = new FakeReadThroughCache();

        await GetAvailableVehiclesHandler.Handle(
            new GetAvailableVehicles(), cache, new FakeAvailableReadModel(), Options(TimeSpan.FromSeconds(5)), CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(5), Assert.Single(cache.Expirations));
    }

    [Fact]
    public async Task The_cached_value_is_a_concrete_array_so_it_survives_the_serializer()
    {
        // The hybrid adapter serializes what it stores. An array round-trips through System.Text.Json
        // without depending on how a collection interface is materialized.
        var cache = new FakeReadThroughCache();

        var result = await GetAvailableVehiclesHandler.Handle(
            new GetAvailableVehicles(), cache, new FakeAvailableReadModel(One), Options(), CancellationToken.None);

        Assert.IsType<AvailableVehicleView[]>(result);
        var roundTripped = JsonSerializer.Deserialize<AvailableVehicleView[]>(
            JsonSerializer.Serialize((AvailableVehicleView[])result));
        Assert.Equal(result, roundTripped);
    }

    [Fact]
    public void The_query_carries_no_filter_and_no_page()
    {
        // F-7: no filters and no paging, which is what makes one cache entry enough.
        Assert.Empty(typeof(GetAvailableVehicles).GetProperties(BindingFlags.Public | BindingFlags.Instance));
    }

    [Fact]
    public void The_handler_declares_no_unit_of_work_and_no_repository()
    {
        var parameters = typeof(GetAvailableVehiclesHandler)
            .GetMethod(nameof(GetAvailableVehiclesHandler.Handle))!
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToList();

        Assert.DoesNotContain(typeof(MPCore.Persistence.Abstractions.IUnitOfWork), parameters);
        Assert.DoesNotContain(typeof(IVehicleRepository), parameters);
    }
}

/// <summary>
/// Why the module passes a bare key. <c>MPCoreCacheOptions.KeyPrefix</c> is applied by the adapter
/// itself, so prepending it in the module would qualify the key twice and the eviction would then miss
/// the entry the read created. This is MP Core behaviour the Fleet keys depend on, so it is measured
/// rather than assumed.
/// </summary>
public sealed class HybridCacheAdapterKeyTests
{
    private sealed class RecordingHybridCache : Microsoft.Extensions.Caching.Hybrid.HybridCache
    {
        public List<string> Keys { get; } = [];

        public override ValueTask<T> GetOrCreateAsync<TState, T>(
            string key,
            TState state,
            Func<TState, CancellationToken, ValueTask<T>> factory,
            Microsoft.Extensions.Caching.Hybrid.HybridCacheEntryOptions? options = null,
            IEnumerable<string>? tags = null,
            CancellationToken cancellationToken = default)
        {
            Keys.Add(key);
            return factory(state, cancellationToken);
        }

        public override ValueTask SetAsync<T>(
            string key,
            T value,
            Microsoft.Extensions.Caching.Hybrid.HybridCacheEntryOptions? options = null,
            IEnumerable<string>? tags = null,
            CancellationToken cancellationToken = default)
        {
            Keys.Add(key);
            return ValueTask.CompletedTask;
        }

        public override ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            Keys.Add(key);
            return ValueTask.CompletedTask;
        }

        public override ValueTask RemoveByTagAsync(string tag, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private static (MPCore.Caching.Hybrid.HybridCacheAdapter Adapter, RecordingHybridCache Inner) Adapter(string prefix)
    {
        var inner = new RecordingHybridCache();
        return (
            new MPCore.Caching.Hybrid.HybridCacheAdapter(
                inner,
                Microsoft.Extensions.Options.Options.Create(new MPCoreCacheOptions { KeyPrefix = prefix })),
            inner);
    }

    [Fact]
    public async Task The_adapter_applies_the_key_prefix_itself_on_a_removal()
    {
        var (adapter, inner) = Adapter("fleetops:");

        await adapter.RemoveAsync(FleetCacheKeys.AvailableVehicles, CancellationToken.None);

        var key = Assert.Single(inner.Keys);
        Assert.NotEqual(FleetCacheKeys.AvailableVehicles, key);
        Assert.Contains(FleetCacheKeys.AvailableVehicles, key, StringComparison.Ordinal);
        Assert.StartsWith("fleetops:", key, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_read_and_the_removal_qualify_the_same_bare_key_identically()
    {
        // The guarantee the eviction depends on: whatever the prefix, the key the read writes under is
        // the key the removal deletes.
        var (adapter, inner) = Adapter("fleetops:");

        await adapter.GetOrCreateAsync(
            FleetCacheKeys.AvailableVehicles,
            static _ => ValueTask.FromResult(Array.Empty<AvailableVehicleView>()),
            TimeSpan.FromSeconds(30),
            CancellationToken.None);
        await adapter.RemoveAsync(FleetCacheKeys.AvailableVehicles, CancellationToken.None);

        Assert.Equal(2, inner.Keys.Count);
        Assert.Equal(inner.Keys[0], inner.Keys[1]);
    }

    [Fact]
    public async Task A_module_that_prepended_the_prefix_itself_would_qualify_it_twice()
    {
        // The mistake this constant's comment exists to prevent, shown rather than described.
        var (adapter, inner) = Adapter("fleetops:");

        await adapter.RemoveAsync("fleetops:" + FleetCacheKeys.AvailableVehicles, CancellationToken.None);

        Assert.Equal("fleetops:fleetops:" + FleetCacheKeys.AvailableVehicles, Assert.Single(inner.Keys));
    }

    [Fact]
    public async Task With_the_default_empty_prefix_the_stored_key_is_the_module_key()
    {
        var (adapter, inner) = Adapter(string.Empty);

        await adapter.RemoveAsync(FleetCacheKeys.AvailableVehicles, CancellationToken.None);

        Assert.Equal(FleetCacheKeys.AvailableVehicles, Assert.Single(inner.Keys));
    }
}

/// <summary>
/// <c>AvailableVehiclesCacheEvictionHandler</c> (docs/architecture.md, decision 4): one <c>Handle</c> per
/// domain event the plan names, each removing the same key the query handler reads.
/// </summary>
public sealed class AvailableVehiclesCacheEvictionHandlerTests
{
    private static readonly Guid Vehicle = Guid.CreateVersion7();
    private static readonly Guid Mission = Guid.CreateVersion7();

    /// <summary>The six events docs/plans/fleet.md lists under "Domain events", with an instance of each.</summary>
    private static IReadOnlyList<IDomainEvent> EveryDomainEvent() =>
    [
        new VehicleRegistered(Vehicle),
        new VehicleStatusChanged(Vehicle, OperationalStatus.Active, OperationalStatus.Inactive),
        new MaintenanceStarted(Vehicle),
        new MaintenanceCompleted(Vehicle),
        new VehicleCommittedToMission(Vehicle, Mission),
        new VehicleReleasedFromMission(Vehicle, Mission),
    ];

    private static IReadOnlyList<MethodInfo> HandleMethods() =>
        typeof(AvailableVehiclesCacheEvictionHandler)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name == "Handle")
            .ToList();

    [Fact]
    public async Task Every_event_removes_the_one_shared_key()
    {
        foreach (var domainEvent in EveryDomainEvent())
        {
            var handle = Assert.Single(
                HandleMethods(),
                method => method.GetParameters()[0].ParameterType == domainEvent.GetType());
            var cache = new FakeCache();

            await (Task)handle.Invoke(null, [domainEvent, cache, NullLogger.Instance, CancellationToken.None])!;

            // The same constant the query handler reads: one constant, two call sites.
            Assert.Equal(FleetCacheKeys.AvailableVehicles, Assert.Single(cache.Removed));
        }
    }

    [Fact]
    public void The_handler_covers_every_domain_event_of_the_module_and_nothing_else()
    {
        // By reflection over the module's own event types, so a seventh domain event cannot be added
        // without either a Handle method or this assertion failing.
        var moduleEvents = Fserp.FleetOperations.Modules.Fleet.AssemblyReference.Assembly
            .GetTypes()
            .Where(type => typeof(IDomainEvent).IsAssignableFrom(type) && !type.IsAbstract)
            .Select(type => type.Name)
            .Order()
            .ToList();
        var handled = HandleMethods()
            .Select(method => method.GetParameters()[0].ParameterType.Name)
            .Order()
            .ToList();

        Assert.Equal(
            [
                "MaintenanceCompleted", "MaintenanceStarted", "VehicleCommittedToMission",
                "VehicleRegistered", "VehicleReleasedFromMission", "VehicleStatusChanged",
            ],
            moduleEvents);
        Assert.Equal(moduleEvents, handled);
    }

    [Fact]
    public void Every_Handle_takes_the_cache_port_the_logger_and_a_cancellation_token_and_nothing_else()
    {
        // The logger joined the list when decision 4 was amended: a removal that fails during a Redis
        // outage is logged rather than rethrown (CacheEvictionOutageTests).
        Assert.NotEmpty(HandleMethods());
        foreach (var method in HandleMethods())
        {
            var parameters = method.GetParameters().Select(parameter => parameter.ParameterType).ToList();
            Assert.Equal(4, parameters.Count);
            Assert.Equal(typeof(ICache), parameters[1]);
            Assert.Equal(typeof(ILogger), parameters[2]);
            Assert.Equal(typeof(CancellationToken), parameters[3]);
        }
    }

    [Fact]
    public async Task Removing_twice_is_harmless_so_at_least_once_delivery_is_safe()
    {
        var cache = new FakeCache();

        await AvailableVehiclesCacheEvictionHandler.Handle(new MaintenanceStarted(Vehicle), cache, NullLogger.Instance, CancellationToken.None);
        await AvailableVehiclesCacheEvictionHandler.Handle(new MaintenanceStarted(Vehicle), cache, NullLogger.Instance, CancellationToken.None);

        Assert.Equal([FleetCacheKeys.AvailableVehicles, FleetCacheKeys.AvailableVehicles], cache.Removed);
    }
}
