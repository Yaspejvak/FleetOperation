using Fserp.FleetOperations.Modules.Fleet.Application;
using Fserp.FleetOperations.Modules.Fleet.Application.Ports;
using Fserp.FleetOperations.Modules.Fleet.Domain;
using Fserp.FleetOperations.Modules.Fleet.Domain.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MPCore.Application.Time;
using MPCore.Audit;
using MPCore.Caching.Abstractions;
using MPCore.Persistence.Abstractions;
using Wolverine;

namespace Fserp.FleetOperations.UnitTests.Fleet;

/// <summary>
/// That the eviction handler is actually <em>routed</em>, not merely written. Wolverine discovers a
/// handler class only when its name ends in <c>Handler</c> or <c>Consumer</c> and only in an assembly the
/// host names, so a correctly shaped class can still never run. Here the Fleet assembly is discovered by
/// a real Wolverine runtime in <see cref="DurabilityMode.MediatorOnly"/> — no PostgreSQL message store,
/// no listeners — and each of the six events is sent through the bus.
/// </summary>
/// <remarks>
/// This proves routing and execution in process. It does not prove delivery <em>after commit</em> from
/// the durable local queue, which needs the real host, PostgreSQL and Wolverine's persistence.
/// </remarks>
public sealed class CacheEvictionRoutingTests : IAsyncLifetime
{
    private readonly FakeCache _cache = new();
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                // The ports every Fleet handler declares, so the runtime can build each chain.
                services.AddSingleton<ICache>(_cache);
                services.AddSingleton<IReadThroughCache>(new FakeReadThroughCache());
                services.AddSingleton<IVehicleRepository, FakeVehicleRepository>();
                services.AddSingleton<IVehicleReadModel>(new FakeAvailableReadModel());
                services.AddSingleton<IUnitOfWork, FakeUnitOfWork>();
                services.AddSingleton<IBusinessAuditRecorder, FakeAuditRecorder>();
                services.AddSingleton<IClock>(new FixedClock(DateTimeOffset.UnixEpoch));
                services.AddOptions<FleetCacheOptions>();
            })
            .UseWolverine(options =>
            {
                options.Durability.Mode = DurabilityMode.MediatorOnly;
                options.Discovery.IncludeAssembly(
                    Fserp.FleetOperations.Modules.Fleet.AssemblyReference.Assembly);
            })
            .StartAsync();
    }

    public Task DisposeAsync() => _host.StopAsync();

    public static TheoryData<object> EveryDomainEvent()
    {
        var vehicle = Guid.CreateVersion7();
        var mission = Guid.CreateVersion7();
        return
        [
            new VehicleRegistered(vehicle),
            new VehicleStatusChanged(vehicle, OperationalStatus.Active, OperationalStatus.Inactive),
            new MaintenanceStarted(vehicle),
            new MaintenanceCompleted(vehicle),
            new VehicleCommittedToMission(vehicle, mission),
            new VehicleReleasedFromMission(vehicle, mission),
        ];
    }

    [Theory]
    [MemberData(nameof(EveryDomainEvent))]
    public async Task Each_domain_event_reaches_the_eviction_handler_and_removes_the_shared_key(object domainEvent)
    {
        await _host.Services.GetRequiredService<IMessageBus>().InvokeAsync(domainEvent);

        Assert.Equal(FleetCacheKeys.AvailableVehicles, Assert.Single(_cache.Removed));
    }
}
