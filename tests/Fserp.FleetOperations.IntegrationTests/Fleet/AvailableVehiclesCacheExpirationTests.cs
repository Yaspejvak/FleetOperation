using System.Diagnostics;
using Fserp.FleetOperations.Modules.Fleet.Application;
using Fserp.FleetOperations.Modules.Fleet.Application.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Fserp.FleetOperations.IntegrationTests.Fleet;

/// <summary>
/// Decision 4's second guarantee: the entry expires after the configured 30 seconds absolute
/// (<see cref="FleetCacheOptions.AvailableVehiclesExpiration"/>, F-6). No test here sleeps for 30
/// seconds; the guarantee is proved in two halves, and each test says which half it is.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><see cref="The_handler_hands_the_cache_the_configured_expiration_and_the_module_configures_thirty_seconds"/>
/// is the <b>value</b> half: the number the real query handler passes to the real cache adapter is
/// <see cref="FleetCacheOptions.AvailableVehiclesExpiration"/>, unmodified, and the value the Fleet
/// module registers is 30 seconds. It proves which lifetime is requested, not that the lifetime is
/// honoured.</item>
/// <item><see cref="An_entry_really_expires_in_Redis_at_the_configured_value_and_the_next_read_is_fresh"/>
/// is the <b>behaviour</b> half: with the same option set to a short value, the entry is alive in Redis
/// at half that value and gone after it, and the read that follows its death is fresh. It proves the
/// lifetime is honoured, at whatever value is configured.</item>
/// </list>
/// <para>
/// Together they say: the lifetime that is honoured is the one configured, and the one configured is
/// 30 seconds. Shortening the option is the only thing that differs from production, which is why the
/// short value is read back out of <see cref="FleetCacheOptions"/> rather than written into an
/// assertion as a literal.
/// </para>
/// <para>
/// No eviction happens anywhere in the behaviour half: the eviction handler is never called, so the
/// only thing that can refresh the entry is its own expiry.
/// </para>
/// </remarks>
/// <param name="database">The PostgreSQL fixture.</param>
/// <param name="redis">The Redis fixture.</param>
[Collection(FleetCacheCollection.Name)]
[Trait(CacheGate.TraitName, CacheGate.TraitValue)]
public sealed class AvailableVehiclesCacheExpirationTests(PostgreSqlFixture database, RedisFixture redis)
{
    /// <summary>
    /// The lifetime the behaviour half composes the cache with. Short enough that a test can watch the
    /// entry die, long enough that a slow machine cannot mistake a slow read for an expiry.
    /// </summary>
    private static readonly TimeSpan ShortExpiration = TimeSpan.FromSeconds(6);

    /// <summary>How long after the expiry the entry is given to disappear before the test calls it a failure.</summary>
    private static readonly TimeSpan Margin = TimeSpan.FromSeconds(4);

    private readonly FleetCacheScenario _scenario = new(database);

    /// <summary>
    /// The value half. The real <c>GetAvailableVehiclesHandler</c> runs against the real hybrid cache
    /// adapter, with the <see cref="FleetCacheOptions"/> the Fleet module itself registered through
    /// <c>AddFleetModule</c>; a recording pass-through in front of the adapter captures the absolute
    /// expiration the handler asked for. The assertion is that the captured value <em>is</em>
    /// <see cref="FleetCacheOptions.AvailableVehiclesExpiration"/> and that the registered value is 30
    /// seconds, so neither the handler nor the module's registration can drift from F-6 unnoticed.
    /// </summary>
    [SkippableFact]
    public async Task The_handler_hands_the_cache_the_configured_expiration_and_the_module_configures_thirty_seconds()
    {
        CacheGate.SkipWhenUnavailable(database, redis);
        await using var cache = new AvailableVehiclesCacheHost(redis.RequireConnectionString());
        await cache.Cache.RemoveAsync(FleetCacheKeys.AvailableVehicles, CancellationToken.None);

        // The options the host runs with: registered by AddFleetModule, not built by this test.
        var moduleOptions = database.Services.GetRequiredService<IOptions<FleetCacheOptions>>();
        var recording = new ExpirationRecordingCache(cache.ReadThrough);

        await _scenario.ReadThroughCacheAsync(recording, moduleOptions);

        var call = Assert.Single(recording.Calls);
        Assert.Equal(FleetCacheKeys.AvailableVehicles, call.Key);
        Assert.Equal<TimeSpan?>(moduleOptions.Value.AvailableVehiclesExpiration, call.Expiration);
        // F-6, stated once more at the only place a change to it would have to pass.
        Assert.Equal(TimeSpan.FromSeconds(30), moduleOptions.Value.AvailableVehiclesExpiration);
    }

    /// <summary>
    /// The behaviour half, observed against Redis itself. The cache is composed with
    /// <see cref="FleetCacheOptions.AvailableVehiclesExpiration"/> set to <see cref="ShortExpiration"/>,
    /// a second vehicle is registered with no eviction of any kind, and then:
    /// <list type="bullet">
    /// <item>the Redis key carries a time-to-live no greater than the configured value — the lifetime
    /// the adapter asked Redis for is the configured one, not the five-minute framework default;</item>
    /// <item>at half the configured value the key is still in Redis and the read still answers with the
    /// pre-registration list — the entry is genuinely cached, so the last step cannot pass vacuously;</item>
    /// <item>after the configured value the key is gone from Redis and the read answers freshly.</item>
    /// </list>
    /// </summary>
    [SkippableFact]
    public async Task An_entry_really_expires_in_Redis_at_the_configured_value_and_the_next_read_is_fresh()
    {
        CacheGate.SkipWhenUnavailable(database, redis);
        await using var cache = new AvailableVehiclesCacheHost(redis.RequireConnectionString(), ShortExpiration);
        var expiration = cache.Options.Value.AvailableVehiclesExpiration;
        Assert.Equal(ShortExpiration, expiration);

        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.RequireConnectionString());
        var server = connection.GetDatabase();
        RedisKey key = cache.RedisKey;

        await cache.Cache.RemoveAsync(FleetCacheKeys.AvailableVehicles, CancellationToken.None);
        var present = await _scenario.RegisterAsync();
        var recording = new ExpirationRecordingCache(cache.ReadThrough);

        var warm = await _scenario.ReadThroughCacheAsync(recording, cache.Options);
        var written = Stopwatch.StartNew();
        Assert.Contains(present.Id, Ids(warm));
        Assert.Equal<TimeSpan?>(expiration, Assert.Single(recording.Calls).Expiration);

        // The adapter writes the shared level as part of the read; look at the server, not the adapter.
        Assert.True(
            await WaitForAsync(() => server.KeyExistsAsync(key), expected: true, TimeSpan.FromSeconds(5)),
            $"The entry was never written to Redis under '{cache.RedisKey}'.");
        var ttl = await server.KeyTimeToLiveAsync(key);
        Assert.NotNull(ttl);
        // The upper bound is the assertion that matters: Redis was asked for the configured lifetime and
        // nothing longer. A value at or below it is what an absolute expiration already partly elapsed looks like.
        Assert.InRange(ttl.Value, TimeSpan.Zero, expiration);

        // Nothing evicts: no eviction handler runs in this test, so only the expiry can refresh the entry.
        var added = await _scenario.RegisterAsync();

        await DelayUntilAsync(written, expiration / 2);
        Assert.True(await server.KeyExistsAsync(key), "The entry died before half its configured lifetime.");
        Assert.DoesNotContain(added.Id, Ids(await _scenario.ReadThroughCacheAsync(cache.ReadThrough, cache.Options)));

        await DelayUntilAsync(written, expiration);
        Assert.True(
            await WaitForAsync(() => server.KeyExistsAsync(key), expected: false, Margin),
            $"The entry outlived its configured expiration of {expiration} in Redis by more than {Margin}.");

        var fresh = await _scenario.ReadThroughCacheAsync(cache.ReadThrough, cache.Options);
        Assert.Contains(added.Id, Ids(fresh));
        Assert.Equal(Ids(await _scenario.ReadFromDatabaseAsync()), Ids(fresh));
    }

    private static async Task DelayUntilAsync(Stopwatch since, TimeSpan elapsed)
    {
        var remaining = elapsed - since.Elapsed;
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining);
        }
    }

    private static async Task<bool> WaitForAsync(Func<Task<bool>> probe, bool expected, TimeSpan within)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < within)
        {
            if (await probe() == expected)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        return await probe() == expected;
    }

    /// <summary>The identities in the order the read model produces them, so two lists compare exactly.</summary>
    private static IReadOnlyList<Guid> Ids(IEnumerable<AvailableVehicleView> list) =>
        list.Select(static row => row.Id).Order().ToList();
}
