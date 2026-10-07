using Fserp.FleetOperations.Modules.Drivers.Application.Contracts;
using Fserp.FleetOperations.Modules.Drivers.Application.Ports;
using Fserp.FleetOperations.Modules.Drivers.Contracts;
using Fserp.FleetOperations.Modules.Drivers.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fserp.FleetOperations.Modules.Drivers.Infrastructure;

/// <summary>The single registration entry point of the Drivers module, called by the host.</summary>
public static class DriversModule
{
    /// <summary>Registers the Drivers module's adapters against the host's one context.</summary>
    /// <typeparam name="TContext">The host's <c>AppDbContext</c>, the single unit-of-work owner.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection.</returns>
    /// <remarks>
    /// Every adapter is registered by type, generic over <typeparamref name="TContext"/>, never with a
    /// lambda: Wolverine refuses a dependency it could only obtain by service location.
    /// <para>
    /// No cache options are registered and no cache port is taken anywhere in this module: nothing about
    /// drivers is cached (D-6, and the plan's query table says "Cached: no" for both queries).
    /// </para>
    /// </remarks>
    public static IServiceCollection AddDriversModule<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IDriverRepository, DriverRepository<TContext>>();
        services.AddScoped<IDriverReadModel, DriverReadModel<TContext>>();
        // The two Contracts ports Operations reaches Drivers through (docs/architecture.md, decision 1).
        // Both are scoped: the commitment port must share the caller's context, so the driver it changes
        // is the row the caller's unit of work commits.
        services.AddScoped<IDriverEligibilityReader, DriverEligibilityReader>();
        services.AddScoped<IDriverCommitments, DriverCommitments>();
        return services;
    }
}
