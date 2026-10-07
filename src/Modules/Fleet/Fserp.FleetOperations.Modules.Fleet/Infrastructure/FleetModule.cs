using Fserp.FleetOperations.Modules.Fleet.Application;
using Fserp.FleetOperations.Modules.Fleet.Application.Contracts;
using Fserp.FleetOperations.Modules.Fleet.Application.Ports;
using Fserp.FleetOperations.Modules.Fleet.Contracts;
using Fserp.FleetOperations.Modules.Fleet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fserp.FleetOperations.Modules.Fleet.Infrastructure;

/// <summary>The single registration entry point of the Fleet module, called by the host.</summary>
public static class FleetModule
{
    /// <summary>Registers the Fleet module's adapters against the host's one context.</summary>
    /// <typeparam name="TContext">The host's <c>AppDbContext</c>, the single unit-of-work owner.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection.</returns>
    /// <remarks>
    /// Every adapter is registered by type, generic over <typeparamref name="TContext"/>, never with a
    /// lambda: Wolverine refuses a dependency it could only obtain by service location.
    /// </remarks>
    public static IServiceCollection AddFleetModule<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IVehicleRepository, VehicleRepository<TContext>>();
        services.AddScoped<IVehicleReadModel, VehicleReadModel<TContext>>();
        // The two Contracts ports other modules reach Fleet through (docs/architecture.md, decision 1).
        // Both are scoped: the commitment port must share the caller's context, so the vehicle it changes
        // is the row the caller's unit of work commits.
        services.AddScoped<IVehicleAvailabilityReader, VehicleAvailabilityReader>();
        services.AddScoped<IVehicleCommitments, VehicleCommitments>();
        // The module's own cache settings with their defaults (F-6: 30 s). No configuration key is
        // required; a deployment that wants another bound binds this section itself.
        services.AddOptions<FleetCacheOptions>();
        return services;
    }
}
