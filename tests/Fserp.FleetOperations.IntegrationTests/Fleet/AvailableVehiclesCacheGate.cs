using System.Collections.Concurrent;
using Fserp.FleetOperations.Modules.Fleet.Application;
using Fserp.FleetOperations.Modules.Fleet.Application.Commands;
using Fserp.FleetOperations.Modules.Fleet.Application.Ports;
using Fserp.FleetOperations.Modules.Fleet.Application.Queries;
using Fserp.FleetOperations.Modules.Fleet.Application.Views;
using Fserp.FleetOperations.Modules.Fleet.Contracts;
using Fserp.FleetOperations.Modules.Fleet.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MPCore.Application.Time;
using MPCore.Audit;
using MPCore.Caching.Abstractions;
using MPCore.Caching.Hybrid;
using MPCore.Persistence.Abstractions;

namespace Fserp.FleetOperations.IntegrationTests.Fleet;

/// <summary>
/// The collection of the cache gate of <c>docs/architecture.md</c> decision 4. It needs PostgreSQL
/// <em>and</em> Redis, because the three behaviours are about a real read model behind a real cache.
/// </summary>
/// <remarks>
/// Parallelization is disabled for this collection. Its three test classes share one cache key —
/// <see cref="FleetCacheKeys.AvailableVehicles"/>, the only key decision 4 declares — and one available
/// list, so running them beside another collection against the same database would let one test's
/// arrangement move another test's list.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FleetCacheCollection : ICollectionFixture<PostgreSqlFixture>, ICollectionFixture<RedisFixture>
{
    /// <summary>The collection name.</summary>
    public const string Name = "Fleet available-vehicles cache (PostgreSQL + Redis)";
}

/// <summary>
/// What the cache gate's tests share: the trait that selects them, and the skip reason that names
/// precisely which dependency was missing.
/// </summary>
public static class CacheGate
{
    /// <summary>The trait name every cache-gate test carries.</summary>
    public const string TraitName = "Category";

    /// <summary>
    /// The trait value every cache-gate test carries, so <c>--filter "Category=RedisCacheGate"</c>
    /// selects these tests and nothing else in the repository.
    /// </summary>
    public const string TraitValue = "RedisCacheGate";

    /// <summary>Skips the calling test when either dependency is missing, naming which one.</summary>
    /// <param name="database">The PostgreSQL fixture.</param>
    /// <param name="redis">The Redis fixture.</param>
    public static void SkipWhenUnavailable(PostgreSqlFixture database, RedisFixture redis)
    {
        var reason = UnavailableReason(database, redis);
        Skip.If(reason is not null, reason);
    }

    /// <summary>
    /// Why the cache gate cannot run here, or <see langword="null"/> when it can. The text names the
    /// missing dependency by name — PostgreSQL, Redis, or both — and repeats each fixture's own reason,
    /// so a reader on another machine knows exactly what to provide.
    /// </summary>
    /// <param name="database">The PostgreSQL fixture.</param>
    /// <param name="redis">The Redis fixture.</param>
    public static string? UnavailableReason(PostgreSqlFixture database, RedisFixture redis)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(redis);

        var missing = new List<string>();
        var details = new List<string>();
        if (database.UnavailableReason is not null)
        {
            missing.Add("PostgreSQL");
            details.Add(database.UnavailableReason);
        }

        if (redis.UnavailableReason is not null)
        {
            missing.Add("Redis");
            details.Add(redis.UnavailableReason);
        }

        return missing.Count == 0
            ? null
            : "The available-vehicles cache gate (docs/architecture.md, decision 4) needs PostgreSQL and "
              + $"Redis together; missing: {string.Join(" and ", missing)}. {string.Join(" | ", details)}";
    }
}

/// <summary>
/// One hybrid cache over the real Redis, composed exactly as <c>AddInfrastructure</c> composes the
/// host's: <c>AddMPCoreHybridCache</c> and nothing substituted. The expiration is the only thing a test
/// chooses, and it is chosen through <see cref="FleetCacheOptions"/>, the production option object.
/// </summary>
internal sealed class AvailableVehiclesCacheHost : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    /// <summary>Composes the cache.</summary>
    /// <param name="redisConnectionString">The Redis the shared level uses.</param>
    /// <param name="expiration">
    /// The value <see cref="FleetCacheOptions.AvailableVehiclesExpiration"/> carries. Left null, the
    /// module's own default applies, which is what the host runs with.
    /// </param>
    public AvailableVehiclesCacheHost(string redisConnectionString, TimeSpan? expiration = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(Logs));
        services.AddMPCoreHybridCache(redisConnectionString);
        var options = services.AddOptions<FleetCacheOptions>();
        if (expiration is not null)
        {
            options.Configure(settings => settings.AvailableVehiclesExpiration = expiration.Value);
        }

        _services = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    /// <summary>Everything the cache's own components logged.</summary>
    public CapturingLoggerProvider Logs { get; } = new();

    /// <summary>The eviction port, as the eviction handler receives it.</summary>
    public ICache Cache => _services.GetRequiredService<ICache>();

    /// <summary>The read-through port, as the query handler receives it.</summary>
    public IReadThroughCache ReadThrough => _services.GetRequiredService<IReadThroughCache>();

    /// <summary>The module's cache settings.</summary>
    public IOptions<FleetCacheOptions> Options => _services.GetRequiredService<IOptions<FleetCacheOptions>>();

    /// <summary>
    /// The key the entry actually occupies in Redis: <see cref="FleetCacheKeys.AvailableVehicles"/>
    /// qualified by <c>MPCoreCacheOptions.KeyPrefix</c>, which the adapter applies and which is empty
    /// unless a deployment sets it. <c>RedisCacheOptions.InstanceName</c> is never set by
    /// <c>AddMPCoreHybridCache</c>, so nothing further is prepended on the way to the server.
    /// </summary>
    public string RedisKey =>
        _services.GetRequiredService<IOptions<MPCoreCacheOptions>>().Value.Qualify(FleetCacheKeys.AvailableVehicles);

    public ValueTask DisposeAsync() => _services.DisposeAsync();
}

/// <summary>
/// The arrangements the cache gate needs, each performed by the real production path: the real command
/// handlers, the real repository, the real <see cref="IVehicleCommitments"/> port and the real
/// <see cref="IVehicleReadModel"/> over PostgreSQL. Nothing about the available list is faked, because
/// the gate exists to prove the real path.
/// </summary>
/// <param name="database">The PostgreSQL fixture.</param>
internal sealed class FleetCacheScenario(PostgreSqlFixture database)
{
    private static string UniquePlate() => "CG-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();

    /// <summary>Registers a vehicle and commits it. It is Active, not under maintenance and uncommitted.</summary>
    /// <param name="capacityKg">The capacity.</param>
    public async Task<VehicleView> RegisterAsync(decimal capacityKg = 1000m)
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

    /// <summary>Changes a vehicle's operational status and commits it.</summary>
    /// <param name="vehicleId">The vehicle.</param>
    /// <param name="status">The requested status.</param>
    public async Task ChangeStatusAsync(Guid vehicleId, OperationalStatus status)
    {
        await using var scope = database.Scope();
        var result = await ChangeVehicleStatusHandler.Handle(
            new ChangeVehicleStatus(vehicleId, status),
            scope.ServiceProvider.GetRequiredService<IVehicleRepository>(),
            scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
            scope.ServiceProvider.GetRequiredService<IBusinessAuditRecorder>(),
            CancellationToken.None);
        Assert.True(result.IsSuccess);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
    }

    /// <summary>Puts a vehicle under maintenance and commits it.</summary>
    /// <param name="vehicleId">The vehicle.</param>
    public async Task StartMaintenanceAsync(Guid vehicleId)
    {
        await using var scope = database.Scope();
        var result = await StartMaintenanceHandler.Handle(
            new StartMaintenance(vehicleId),
            scope.ServiceProvider.GetRequiredService<IVehicleRepository>(),
            scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
            scope.ServiceProvider.GetRequiredService<IBusinessAuditRecorder>(),
            CancellationToken.None);
        Assert.True(result.IsSuccess);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
    }

    /// <summary>Takes a vehicle out of maintenance and commits it.</summary>
    /// <param name="vehicleId">The vehicle.</param>
    public async Task CompleteMaintenanceAsync(Guid vehicleId)
    {
        await using var scope = database.Scope();
        var result = await CompleteMaintenanceHandler.Handle(
            new CompleteMaintenance(vehicleId),
            scope.ServiceProvider.GetRequiredService<IVehicleRepository>(),
            scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
            scope.ServiceProvider.GetRequiredService<IBusinessAuditRecorder>(),
            CancellationToken.None);
        Assert.True(result.IsSuccess);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
    }

    /// <summary>
    /// Commits a vehicle to a mission through <see cref="IVehicleCommitments"/>, the Contracts port
    /// Operations calls inside the Assign transaction, and commits the unit of work as that transaction
    /// would.
    /// </summary>
    /// <param name="vehicleId">The vehicle.</param>
    /// <param name="missionId">The mission.</param>
    /// <param name="requiredCapacityKg">The capacity the mission requires.</param>
    public async Task CommitToMissionAsync(Guid vehicleId, Guid missionId, decimal requiredCapacityKg = 100m)
    {
        await using var scope = database.Scope();
        await scope.ServiceProvider.GetRequiredService<IVehicleCommitments>()
            .CommitToMissionAsync(vehicleId, missionId, requiredCapacityKg, CancellationToken.None);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
    }

    /// <summary>Releases a vehicle from the mission that holds it, through the same Contracts port.</summary>
    /// <param name="vehicleId">The vehicle.</param>
    /// <param name="missionId">The mission.</param>
    public async Task ReleaseFromMissionAsync(Guid vehicleId, Guid missionId)
    {
        await using var scope = database.Scope();
        await scope.ServiceProvider.GetRequiredService<IVehicleCommitments>()
            .ReleaseFromMissionAsync(vehicleId, missionId, CancellationToken.None);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
    }

    /// <summary>
    /// The available list straight from PostgreSQL, with no cache in the way. Used only to say what the
    /// fresh answer is, never as the thing under test.
    /// </summary>
    public async Task<IReadOnlyList<AvailableVehicleView>> ReadFromDatabaseAsync()
    {
        await using var scope = database.Scope();
        return await scope.ServiceProvider.GetRequiredService<IVehicleReadModel>()
            .GetAvailableAsync(CancellationToken.None);
    }

    /// <summary>
    /// The available list exactly as <c>GET /api/fleet/vehicles/available</c> produces it: the real
    /// <see cref="GetAvailableVehiclesHandler"/>, the real read-through cache and the real read model.
    /// </summary>
    /// <param name="cache">The read-through cache the handler is given.</param>
    /// <param name="options">The module's cache settings.</param>
    public async Task<IReadOnlyList<AvailableVehicleView>> ReadThroughCacheAsync(
        IReadThroughCache cache,
        IOptions<FleetCacheOptions> options)
    {
        await using var scope = database.Scope();
        return await GetAvailableVehiclesHandler.Handle(
            new GetAvailableVehicles(),
            cache,
            scope.ServiceProvider.GetRequiredService<IVehicleReadModel>(),
            options,
            CancellationToken.None);
    }
}

/// <summary>One log record, reduced to what an assertion can state about it.</summary>
/// <param name="Category">The logger category.</param>
/// <param name="Level">The level.</param>
/// <param name="EventId">The numeric event id.</param>
/// <param name="EventName">The event name, when the record carries one.</param>
/// <param name="Message">The rendered message.</param>
/// <param name="ExceptionType">The exception's type name, when there is one.</param>
/// <param name="ExceptionMessage">The exception's message, when there is one.</param>
public sealed record CapturedLog(
    string Category,
    LogLevel Level,
    int EventId,
    string? EventName,
    string Message,
    string? ExceptionType,
    string? ExceptionMessage)
{
    /// <summary>A one-line form, for a failure message that has to explain what was logged instead.</summary>
    public override string ToString() =>
        $"{Level} {Category}[{EventId}{(EventName is null ? string.Empty : "/" + EventName)}] {Message}"
        + (ExceptionType is null ? string.Empty : $" -> {ExceptionType}: {ExceptionMessage}");
}

/// <summary>
/// Captures every log record written through the composition it is added to. The outage test asserts on
/// log records rather than on stdout, so the assertion is about what the host logged and not about what
/// a console happened to render.
/// </summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLog> _records = new();

    /// <summary>Everything captured so far, oldest first.</summary>
    public IReadOnlyCollection<CapturedLog> Records => _records;

    /// <summary>Forgets everything captured so far, so a phase of a test can be measured on its own.</summary>
    public void Clear() => _records.Clear();

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _records);

    /// <inheritdoc />
    public void Dispose() => GC.SuppressFinalize(this);

    private sealed class CapturingLogger(string category, ConcurrentQueue<CapturedLog> records) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            records.Enqueue(new CapturedLog(
                category,
                logLevel,
                eventId.Id,
                eventId.Name,
                formatter(state, exception),
                exception?.GetType().FullName,
                exception?.Message));
        }
    }
}

/// <summary>
/// Passes every call to the real read-through cache and records the absolute expiration the caller
/// asked for. It decides nothing: the value it records is the value the real adapter receives.
/// </summary>
/// <param name="inner">The real cache.</param>
internal sealed class ExpirationRecordingCache(IReadThroughCache inner) : IReadThroughCache
{
    private readonly ConcurrentQueue<(string Key, TimeSpan? Expiration)> _calls = new();

    /// <summary>Every <c>GetOrCreateAsync</c> the cache was asked for, in order.</summary>
    public IReadOnlyCollection<(string Key, TimeSpan? Expiration)> Calls => _calls;

    /// <inheritdoc />
    public ValueTask<T> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, ValueTask<T>> factory,
        TimeSpan? absoluteExpiration = null,
        CancellationToken cancellationToken = default)
    {
        _calls.Enqueue((key, absoluteExpiration));
        return inner.GetOrCreateAsync(key, factory, absoluteExpiration, cancellationToken);
    }
}
