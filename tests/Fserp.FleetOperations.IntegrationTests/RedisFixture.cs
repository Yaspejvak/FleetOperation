using Testcontainers.Redis;

namespace Fserp.FleetOperations.IntegrationTests;

/// <summary>
/// One Redis for the test run, reached exactly as the host reaches it: a StackExchange.Redis
/// configuration string handed to <c>AddMPCoreHybridCache</c>. Nothing is composed here — the fixture
/// owns only the server, because the cache gate needs several different compositions over the one store.
/// </summary>
/// <remarks>
/// The escape hatch is the same shape as <see cref="PostgreSqlFixture"/>'s: <see cref="ConnectionStringVariable"/>
/// when set names a reachable Redis, otherwise a Testcontainers container is started, which needs Docker.
/// With neither, <see cref="UnavailableReason"/> says which of the two was missing and every test that
/// depends on it reports Skipped; none passes vacuously.
/// </remarks>
public sealed class RedisFixture : IAsyncLifetime
{
    /// <summary>A StackExchange.Redis configuration string to a disposable Redis; when set, no container is started.</summary>
    public const string ConnectionStringVariable = "FLEETOPS_TEST_REDIS";

    private RedisContainer? _container;

    /// <summary>Why the Redis-backed tests cannot run here, or <see langword="null"/> when they can.</summary>
    public string? UnavailableReason { get; private set; }

    /// <summary>
    /// The configuration string of the reachable Redis, or <see langword="null"/> when none is.
    /// It is never written to a tracked file and never logged.
    /// </summary>
    public string? ConnectionString { get; private set; }

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            ConnectionString = connectionString;
            return;
        }

        try
        {
            _container = new RedisBuilder("redis:7-alpine").Build();
            await _container.StartAsync();
            ConnectionString = _container.GetConnectionString();
        }
        catch (Exception exception)
        {
            UnavailableReason =
                $"No Redis available: set {ConnectionStringVariable} or make Docker available for Testcontainers "
                + $"({exception.GetType().Name}: {exception.Message})";
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>Skips the calling test, with the reason, when no Redis is available.</summary>
    public void SkipWhenUnavailable() => Skip.If(UnavailableReason is not null, UnavailableReason);

    /// <summary>The configuration string, or an exception naming why there is none.</summary>
    public string RequireConnectionString() =>
        ConnectionString ?? throw new InvalidOperationException(UnavailableReason ?? "Redis is unavailable.");
}
