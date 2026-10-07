using Microsoft.EntityFrameworkCore;
using MPCore.Audit.EntityFrameworkCore;
using MPCore.Domain.Events;
using MPCore.Persistence.EntityFrameworkCore.PostgreSql;

namespace Fserp.FleetOperations.Infrastructure.Persistence;

public sealed class AppDbContext(
    DbContextOptions<AppDbContext> options,
    TimeProvider timeProvider,
    IAggregateEventSink eventSink)
    : MPCoreDbContext(options, timeProvider, eventSink)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // One line per bounded-context module: its Infrastructure folder holds its EF mappings.
        //     modelBuilder.ApplyConfigurationsFromAssembly(Fserp.FleetOperations.Modules.Billing.AssemblyReference.Assembly);
        modelBuilder.ApplyConfigurationsFromAssembly(Fserp.FleetOperations.Modules.Fleet.AssemblyReference.Assembly);
        modelBuilder.ApplyConfigurationsFromAssembly(Fserp.FleetOperations.Modules.Drivers.AssemblyReference.Assembly);
        modelBuilder.ApplyConfigurationsFromAssembly(Fserp.FleetOperations.Modules.Operations.AssemblyReference.Assembly);
        modelBuilder.ApplyConfigurationsFromAssembly(Fserp.FleetOperations.Modules.Administration.AssemblyReference.Assembly);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        modelBuilder.ApplyMPCoreAudit();
        base.OnModelCreating(modelBuilder);
    }
}
