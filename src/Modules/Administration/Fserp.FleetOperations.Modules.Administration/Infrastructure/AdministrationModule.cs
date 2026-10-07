using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fserp.FleetOperations.Modules.Administration.Infrastructure;

/// <summary>The single registration entry point of the Administration module, called by the host.</summary>
public static class AdministrationModule
{
    /// <summary>Registers the Administration module's adapters against the host's one context.</summary>
    /// <typeparam name="TContext">The host's <c>AppDbContext</c>, the single unit-of-work owner.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection.</returns>
    /// <remarks>
    /// <para>
    /// <b>This module registers no adapter, deliberately.</b> It owns no aggregate, no table and no read
    /// model: it reads the trail through <c>IAuditQuery</c>, a port MP Core owns and already registers.
    /// <c>AddMPCoreAudit&lt;AppDbContext&gt;(...)</c> in
    /// <c>Fserp.FleetOperations.Infrastructure.DependencyInjection.AddInfrastructure</c> registers
    /// <c>IAuditQuery</c> as scoped <c>EntityFrameworkAuditQuery&lt;AppDbContext&gt;</c>, together with the
    /// sink, the recorder and the interceptor, because <c>businessAudit</c> is <c>postgresql</c> for this
    /// repository.
    /// </para>
    /// <para>
    /// Registering it a second time here would need this project to reference
    /// <c>MPCore.Audit.EntityFrameworkCore.PostgreSql</c> — a persistence provider inside a module that
    /// owns no persistence — and would duplicate a registration the host composition already makes. The
    /// shape is proved rather than asserted: <c>AdministrationCompositionTests</c> resolves
    /// <c>IAuditQuery</c> from the real host container.
    /// </para>
    /// <para>
    /// The query handler needs no registration either. Wolverine discovers
    /// <c>GetAuditEntriesHandler</c> from this module's assembly, which the host lists in
    /// <c>Hosting/HandlerAssemblies.cs</c>; the same list feeds <c>AddMPCoreValidators</c>, which finds
    /// <c>GetAuditEntriesValidator</c>.
    /// </para>
    /// <para>
    /// When this module does acquire an adapter, register it by type and generic over
    /// <typeparamref name="TContext"/>, never with a lambda — Wolverine refuses a dependency it could only
    /// obtain by service location.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddAdministrationModule<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        return services;
    }
}
