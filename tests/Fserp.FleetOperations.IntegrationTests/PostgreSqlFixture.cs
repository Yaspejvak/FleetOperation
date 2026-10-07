using Fserp.FleetOperations.Infrastructure.Audit;
using Fserp.FleetOperations.Infrastructure.Persistence;
using Fserp.FleetOperations.Modules.Drivers.Infrastructure;
using Fserp.FleetOperations.Modules.Fleet.Infrastructure;
using Fserp.FleetOperations.Modules.Operations.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MPCore.Application.Time;
using MPCore.Audit.EntityFrameworkCore;
using MPCore.Domain.Events;
using MPCore.Persistence.Abstractions;
using MPCore.Persistence.EntityFrameworkCore.PostgreSql;
using MPCore.Security;
using Testcontainers.PostgreSql;

namespace Fserp.FleetOperations.IntegrationTests;

/// <summary>
/// One PostgreSQL for the test run, migrated with the repository's own migrations, and the Fleet
/// persistence composition around it: the host's <see cref="AppDbContext"/> with the audit interceptor,
/// the audit recorder and query, and the Fleet module's adapters. Wolverine and the HTTP host are not
/// started; each test plays the middleware's part by saving (or not saving) the unit of work itself.
/// </summary>
public sealed class PostgreSqlFixture : IAsyncLifetime
{
    /// <summary>A connection string to a disposable database; when set, no container is started.</summary>
    public const string ConnectionStringVariable = "FLEETOPS_TEST_POSTGRES";

    /// <summary>The subject the test actor carries, as the validated token would.</summary>
    public const string ActorSubject = "integration-test-user";

    private PostgreSqlContainer? _container;
    private ServiceProvider? _services;

    /// <summary>Why the tests cannot run here, or <see langword="null"/> when they can.</summary>
    public string? UnavailableReason { get; private set; }

    /// <summary>
    /// The connection string of the migrated database, or <see langword="null"/> when none is reachable.
    /// Round 12's outage test composes the real host around it; it is never written to a tracked file.
    /// </summary>
    public string? ConnectionString { get; private set; }

    /// <summary>The composed services.</summary>
    public IServiceProvider Services => _services ?? throw new InvalidOperationException(UnavailableReason);

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            try
            {
                _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
                await _container.StartAsync();
                connectionString = _container.GetConnectionString();
            }
            catch (Exception exception)
            {
                UnavailableReason =
                    $"No PostgreSQL available: set {ConnectionStringVariable} or make Docker available for Testcontainers "
                    + $"({exception.GetType().Name}: {exception.Message})";
                return;
            }
        }

        ConnectionString = connectionString;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<ICurrentActorAccessor, TestActorAccessor>();
        services.AddSingleton<IAggregateEventSink>(NullAggregateEventSink.Instance);
        services.AddMPCoreAudit<AppDbContext>(AuditPolicyConfiguration.Configure);
        services.AddDbContext<AppDbContext>((provider, options) =>
            PostgreSqlDbContextOptions.Apply(options, connectionString).UseMPCoreAudit(provider));
        services.AddScoped<IUnitOfWork>(static provider => provider.GetRequiredService<AppDbContext>());
        services.AddFleetModule<AppDbContext>();
        // Round 5: the Drivers module's adapters and its two Contracts ports, composed the same way.
        services.AddDriversModule<AppDbContext>();
        // Round 7: the Operations module's repository and read model. Its handlers reach Fleet and Drivers
        // through the Contracts ports the two registrations above supply, in one scope and therefore one
        // AppDbContext — which is what makes the Assign transaction a single transaction here as in the host.
        services.AddOperationsModule<AppDbContext>();
        _services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>Skips the calling test, with the reason, when no PostgreSQL is available.</summary>
    public void SkipWhenUnavailable() => Skip.If(UnavailableReason is not null, UnavailableReason);

    /// <summary>A new scope: one context, one unit of work, as one request would have.</summary>
    public AsyncServiceScope Scope() => Services.CreateAsyncScope();

    private sealed class TestActorAccessor : ICurrentActorAccessor
    {
        public CurrentActor Current { get; } =
            new CurrentActorBuilder(ActorKind.User) { SubjectId = ActorSubject, UserName = "integration" }.Build();
    }
}

[CollectionDefinition(Name)]
public sealed class PostgreSqlCollection : ICollectionFixture<PostgreSqlFixture>
{
    public const string Name = "PostgreSQL";
}
