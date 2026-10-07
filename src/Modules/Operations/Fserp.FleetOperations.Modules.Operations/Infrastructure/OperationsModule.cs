using Fserp.FleetOperations.Modules.Operations.Application.Ports;
using Fserp.FleetOperations.Modules.Operations.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fserp.FleetOperations.Modules.Operations.Infrastructure;

/// <summary>The single registration entry point of the Operations module, called by the host.</summary>
public static class OperationsModule
{
    /// <summary>Registers the Operations module's adapters against the host's one context.</summary>
    /// <typeparam name="TContext">The host's <c>AppDbContext</c>, the single unit-of-work owner.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection.</returns>
    /// <remarks>
    /// Every adapter is registered by type, generic over <typeparamref name="TContext"/>, never with a
    /// lambda: Wolverine refuses a dependency it could only obtain by service location.
    /// <para>
    /// The module publishes no Contracts interface — nobody calls Operations — and registers no cache
    /// adapter or cache options: nothing in Operations is cached.
    /// </para>
    /// <para>
    /// Fleet's and Drivers' Contracts ports, which <c>AssignMission</c>, <c>CompleteMission</c> and
    /// <c>CancelMission</c> depend on, are registered by those modules' own entry points. The repository is
    /// scoped so the mission it loads is tracked by the same context the commitment ports write through.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddOperationsModule<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IMissionRepository, MissionRepository<TContext>>();
        services.AddScoped<IMissionReadModel, MissionReadModel<TContext>>();
        return services;
    }
}
