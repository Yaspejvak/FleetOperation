using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Fserp.FleetOperations.Modules.Fleet.Application;
using Fserp.FleetOperations.Modules.Fleet.Application.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MPCore.Caching.Abstractions;

namespace Fserp.FleetOperations.IntegrationTests.Fleet;

/// <summary>
/// Decision 4's third guarantee: with Redis stopped mid-run, <c>GET /api/fleet/vehicles/available</c>
/// still answers <c>200</c> — served from the in-process level or from PostgreSQL — and the outage is
/// logged.
/// </summary>
/// <remarks>
/// <para>
/// <b>How the outage is induced.</b> The host's cache is not pointed at Redis directly. It is pointed at
/// <see cref="RedisOutageProxy"/>, a loopback TCP forwarder started by the test, which relays every byte
/// to the real Redis wherever it lives — a Testcontainers container or the hosted server named by
/// <c>FLEETOPS_TEST_REDIS</c>. Mid-test, <see cref="RedisOutageProxy.Cut"/> stops the listener and
/// closes every live socket with a reset. From that instant the cache's connection is dead and every
/// reconnection attempt is refused by the loopback stack, which is exactly what a stopped Redis looks
/// like to a client. Nothing is mocked and no exception is injected: the failure the cache sees is
/// produced by the operating system's TCP stack, so this test works identically on a machine with
/// Docker and on one reaching a hosted Redis. A <c>container.StopAsync()</c> would have worked only in
/// the first case.
/// </para>
/// <para>
/// <b>Why the cache is cold when the outage starts.</b> The hybrid adapter answers from its in-process
/// level without consulting Redis at all when that level holds the entry, so a test that simply cut the
/// connection after a read would prove nothing about Redis. The test therefore removes the one decision
/// 4 key through the host's own <see cref="ICache"/> <em>while Redis is still up</em> — which clears
/// both levels — and only then cuts the connection. The request under test is consequently a genuine
/// miss that must consult Redis, fail, and fall back to the factory, which is the read model over
/// PostgreSQL.
/// </para>
/// <para>
/// <b>What counts as a pass.</b> The test passes when all five of these hold, and the runner prints
/// nothing beyond the test's name and a duration of roughly the Redis client's connect timeout plus the
/// database read:
/// </para>
/// <list type="number">
/// <item>the warm-up request answered <c>200</c> and the forwarder accepted at least one connection, so
/// the cache really was talking to Redis through it and the forwarder is transparent;</item>
/// <item>nothing resembling a cache failure was logged before the cut, so the record asserted below is
/// caused by the outage and not by a forwarder that never worked;</item>
/// <item>the request made during the outage answered <c>200</c>;</item>
/// <item>its body is the list PostgreSQL answers with, compared by vehicle identity;</item>
/// <item>at least one log record at <see cref="LogLevel.Warning"/> or above names the cache failure.
/// In MP Core 0.9.3 that record is <c>Microsoft.Extensions.Caching.Hybrid.HybridCache</c>, event 6
/// <c>CacheBackendReadFailure</c>, "Cache backend read failure.", at <see cref="LogLevel.Error"/>,
/// carrying the <c>StackExchange.Redis.RedisConnectionException</c>; the assertion is deliberately
/// broader than that so that an equivalent record from another component still satisfies it.</item>
/// </list>
/// <para>
/// <b>What a failure prints.</b> A non-<c>200</c> prints the status and the whole response body, which
/// for a Problem Details answer names the error domain and code — that is the case decision 4 calls out
/// as "if the adapter throws instead, that is reported, not hidden". A missing log record prints every
/// record at Warning or above that <em>was</em> captured during the outage, one per line, as
/// <c>Level Category[eventId/eventName] message -&gt; ExceptionType: message</c>, so the reader can see
/// what the host said instead. A wrong list prints the expected and actual identities.
/// </para>
/// <para>
/// The request is bounded by <see cref="RequestTimeout"/>. Decision 4 expects the answer to be slower
/// during an outage, by about the Redis client's connect timeout; the test measures that latency and
/// reports it in every failure message so the runbook can quote a real number.
/// </para>
/// </remarks>
/// <param name="database">The PostgreSQL fixture.</param>
/// <param name="redis">The Redis fixture.</param>
[Collection(FleetCacheCollection.Name)]
[Trait(CacheGate.TraitName, CacheGate.TraitValue)]
public sealed class AvailableVehiclesRedisOutageTests(PostgreSqlFixture database, RedisFixture redis)
{
    /// <summary>The longest the outage request may take before the test calls it a hang rather than a delay.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(120);

    private const string AvailableVehiclesRoute = "/api/fleet/vehicles/available";

    private readonly FleetCacheScenario _scenario = new(database);

    [SkippableFact]
    public async Task The_available_list_still_answers_200_from_PostgreSQL_when_Redis_stops_mid_run_and_the_outage_is_logged()
    {
        CacheGate.SkipWhenUnavailable(database, redis);

        await using var proxy = RedisOutageProxy.Start(redis.RequireConnectionString());
        await using var host = new CacheOutageHostFactory(
            database.ConnectionString ?? throw new InvalidOperationException("The database fixture has no connection string."),
            proxy.ConnectionString);
        using var client = host.Client(CacheOutageHostFactory.OperatorRole);

        // One vehicle that must be in every answer below, so "the list is still correct" is a statement
        // about a row this test put there and not about an empty list.
        var vehicle = await _scenario.RegisterAsync();

        // 1. Redis is up: the request succeeds and the cache really goes through the forwarder.
        var warm = await GetAvailableAsync(client);
        Assert.Equal(HttpStatusCode.OK, warm.Status);
        Assert.Contains(vehicle.Id, warm.Ids);
        Assert.True(
            proxy.ConnectionsAccepted > 0,
            "The cache never connected through the forwarder, so cutting it would not have been an outage.");

        // 2. Clear the one decision 4 key through the host's own cache port, while Redis is still up, so
        //    that both levels are cold and the request under test must consult Redis.
        await host.Services.GetRequiredService<ICache>()
            .RemoveAsync(FleetCacheKeys.AvailableVehicles, CancellationToken.None);

        var beforeTheCut = host.Logs.Records.Where(NamesACacheFailure).ToList();
        Assert.True(
            beforeTheCut.Count == 0,
            "A cache failure was logged while Redis was still up, so the forwarder is not transparent and "
            + "the outage assertion would prove nothing. Captured: " + Describe(beforeTheCut));
        host.Logs.Clear();

        // 3. The outage.
        proxy.Cut();

        var elapsed = Stopwatch.StartNew();
        var outage = await GetAvailableAsync(client);
        elapsed.Stop();
        var latency = elapsed.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);

        Assert.True(
            outage.Status == HttpStatusCode.OK,
            $"GET {AvailableVehiclesRoute} answered {(int)outage.Status} during the Redis outage after {latency}s. "
            + $"Decision 4 requires 200, served from the in-process level or from PostgreSQL. Body: {outage.Body}");

        // 4. The answer is the list PostgreSQL holds: the factory ran, so the fallback is real.
        var expected = (await _scenario.ReadFromDatabaseAsync()).Select(static row => row.Id).Order().ToList();
        Assert.Contains(vehicle.Id, outage.Ids);
        Assert.Equal(expected, outage.Ids);

        // 5. The outage was logged, at Warning or above, naming the cache failure.
        var reported = host.Logs.Records.Where(NamesACacheFailure).ToList();
        Assert.True(
            reported.Count > 0,
            $"The Redis outage was not logged. The request answered 200 after {latency}s, so the fallback "
            + "worked, but decision 4 also requires the failure to be reported. Records at Warning or above "
            + "captured during the outage: "
            + Describe(host.Logs.Records.Where(static record => record.Level >= LogLevel.Warning).ToList()));
    }

    /// <summary>
    /// A log record at Warning or above that names the cache failure. The predicate is broader than the
    /// one record MP Core 0.9.3 happens to produce, so that a different component reporting the same
    /// outage still satisfies decision 4; it is not so broad that an unrelated warning would match.
    /// </summary>
    private static bool NamesACacheFailure(CapturedLog record) =>
        record.Level >= LogLevel.Warning
        && (record.Category.Contains("Cache", StringComparison.OrdinalIgnoreCase)
            || record.Message.Contains("cache", StringComparison.OrdinalIgnoreCase)
            || (record.ExceptionType?.Contains("Redis", StringComparison.OrdinalIgnoreCase) ?? false));

    private static string Describe(IReadOnlyCollection<CapturedLog> records) =>
        records.Count == 0
            ? "(none)"
            : Environment.NewLine + string.Join(Environment.NewLine, records.Select(static record => record.ToString()));

    private static async Task<(HttpStatusCode Status, string Body, IReadOnlyList<Guid> Ids)> GetAvailableAsync(HttpClient client)
    {
        using var cancellation = new CancellationTokenSource(RequestTimeout);
        using var response = await client.GetAsync(new Uri(AvailableVehiclesRoute, UriKind.Relative), cancellation.Token);
        var body = await response.Content.ReadAsStringAsync(cancellation.Token);
        return (response.StatusCode, body, response.StatusCode == HttpStatusCode.OK ? ReadIds(body) : []);
    }

    /// <summary>
    /// The identities in the response body. Read from the JSON rather than through a deserialized copy of
    /// <see cref="AvailableVehicleView"/>, so the test states what a caller receives over the wire.
    /// </summary>
    private static IReadOnlyList<Guid> ReadIds(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.EnumerateArray()
            .Select(static element => element.GetProperty("id").GetGuid())
            .Order()
            .ToList();
    }
}
