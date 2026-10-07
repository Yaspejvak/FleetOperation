using Microsoft.Extensions.DependencyInjection;
using MPCore.Audit.EntityFrameworkCore;
using Fserp.FleetOperations.Infrastructure.Audit;
using MPCore.Caching.Hybrid;
using MPCore.Messaging.Wolverine;
using MPCore.Persistence.EntityFrameworkCore.PostgreSql;
using Fserp.FleetOperations.Infrastructure.Persistence;

namespace Fserp.FleetOperations.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        string connectionString,
        string cacheConnectionString)
    {
        services.AddMPCoreHybridCache(cacheConnectionString);
        // Registered through Wolverine's integration: a handler that takes AppDbContext runs inside its
        // transaction and the messages it publishes are committed with it (transactional outbox).
        // The audit interceptor runs inside AppDbContext, so every SaveChanges writes the entity's
        // audit rows in the same transaction as the change itself.
        services.AddMPCoreWolverineDbContext<AppDbContext>((provider, options) =>
            PostgreSqlDbContextOptions.Apply(options, connectionString).UseMPCoreAudit(provider));
        services.AddMPCoreAudit<AppDbContext>(AuditPolicyConfiguration.Configure);
        return services;
    }
}
