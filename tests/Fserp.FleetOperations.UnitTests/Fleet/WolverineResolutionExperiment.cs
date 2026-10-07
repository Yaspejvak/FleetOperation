using Fserp.FleetOperations.Modules.Fleet.Application;
using Fserp.FleetOperations.Modules.Fleet.Application.Commands;
using Fserp.FleetOperations.Modules.Fleet.Application.Ports;
using Fserp.FleetOperations.Modules.Fleet.Application.Queries;
using Fserp.FleetOperations.Modules.Fleet.Application.Views;
using Fserp.FleetOperations.Modules.Fleet.Domain.Events;
using MPCore.Application.Results;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MPCore.Application.Time;
using MPCore.Audit;
using MPCore.Caching.Abstractions;
using MPCore.Persistence.Abstractions;
using Wolverine;
using Xunit.Abstractions;

namespace Fserp.FleetOperations.UnitTests.Fleet;

/// <summary>
/// The round 2-to-4 question, settled by a run rather than by argument: does starting a Wolverine runtime
/// prove that a discovered handler's <em>ports resolve</em>, or only that the handler was
/// <em>discovered</em>? (docs/ai-development-notes.md, unverified item.)
/// </summary>
/// <remarks>
/// The method is a mutation: <see cref="IVehicleRepository"/> is left out of the container that
/// <c>CacheEvictionRoutingTests</c> composes in full. If a start with the port missing still succeeds, a
/// start proves discovery only, and the four handler chains that fixture never invokes remain unverified.
/// <para>
/// The environment is pinned explicitly, because <c>Host.CreateDefaultBuilder()</c> takes
/// <c>ValidateOnBuild</c> and <c>ValidateScopes</c> from <c>IsDevelopment()</c>: without the pin, a throw
/// could come from the container rather than from Wolverine, and the two mean opposite things.
/// </para>
/// </remarks>
public sealed class WolverineResolutionExperiment(ITestOutputHelper output)
{
    /// <summary>Composes the fixture's container, optionally without the vehicle repository.</summary>
    private static IHostBuilder Builder(bool withRepository) =>
        Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            // Step 2: pinned, so ValidateOnBuild/ValidateScopes are off and a throw cannot be the
            // container's doing. "Production" is not Development, which is what decides both flags.
            .UseEnvironment(Environments.Production)
            .ConfigureServices(services =>
            {
                services.AddSingleton<ICache>(new FakeCache());
                services.AddSingleton<IReadThroughCache>(new FakeReadThroughCache());
                if (withRepository)
                {
                    services.AddSingleton<IVehicleRepository, FakeVehicleRepository>();
                }

                services.AddSingleton<IVehicleReadModel>(new EmptyVehicleReadModel());
                services.AddSingleton<IUnitOfWork, FakeUnitOfWork>();
                services.AddSingleton<IBusinessAuditRecorder, FakeAuditRecorder>();
                services.AddSingleton<IClock>(new FixedClock(DateTimeOffset.UnixEpoch));
                services.AddOptions<FleetCacheOptions>();
            })
            .UseWolverine(options =>
            {
                options.Durability.Mode = DurabilityMode.MediatorOnly;
                options.Discovery.IncludeAssembly(Fserp.FleetOperations.Modules.Fleet.AssemblyReference.Assembly);
            });

    private static IEnumerable<Exception> Chain(Exception exception)
    {
        // As UniqueViolations.TryMap and ConcurrencyExceptionMapper.TryMap already do in this host: the
        // outermost type is generated-code wrapping as often as not.
        for (var current = exception; current is not null; current = current.InnerException)
        {
            yield return current;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.SelectMany(Chain))
                {
                    yield return inner;
                }
            }
        }
    }

    private void Report(string step, Exception? exception)
    {
        if (exception is null)
        {
            output.WriteLine($"{step}: no exception.");
            return;
        }

        foreach (var link in Chain(exception))
        {
            output.WriteLine($"{step}: {link.GetType().FullName}: {link.Message}");
        }
    }

    [Fact]
    public async Task Starting_the_runtime_with_a_handler_port_missing_does_not_fail_the_start()
    {
        // Step 1. The mutation: the chain of StartMaintenanceHandler, CompleteMaintenanceHandler,
        // ChangeVehicleStatusHandler and RegisterVehicleHandler cannot be satisfied.
        var start = await Record.ExceptionAsync(async () =>
        {
            using var host = await Builder(withRepository: false).StartAsync();
        });

        Report("start without IVehicleRepository", start);
        Assert.Null(start);
    }

    [Fact]
    public async Task A_message_whose_port_is_missing_fails_only_when_it_is_sent_and_it_fails_as_a_resolution_failure()
    {
        // Steps 3 and 4. The host started; now the handler is actually needed.
        using var host = await Builder(withRepository: false).StartAsync();
        var bus = host.Services.GetRequiredService<IMessageBus>();

        // InvokeAsync<T>, as VehicleEndpoints calls it. The void overload treats the handler's return
        // value as a cascading message to publish, which MediatorOnly mode refuses for its own reasons and
        // which would mask the failure under test.
        var sending = await Record.ExceptionAsync(() =>
            bus.InvokeAsync<MPCore.Application.Results.Result<Fserp.FleetOperations.Modules.Fleet.Application.Views.VehicleView>>(
                new StartMaintenance(Guid.CreateVersion7())));

        Report("send StartMaintenance without IVehicleRepository", sending);
        Assert.NotNull(sending);
        var chain = Chain(sending).ToList();

        // Step 4: which failure, not merely that one occurred. A missing *route* would mean the chain
        // never existed, which is the opposite conclusion; all three routing types are rejected by name.
        Assert.DoesNotContain(chain, link => link.GetType().Name is "IndeterminateRoutesException"
            or "NoHandlerForEndpointException" or "InvalidHandlerException");
        // A resolution failure names the port it could not supply.
        Assert.Contains(chain, link => link.Message.Contains(nameof(IVehicleRepository), StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_same_message_succeeds_once_the_port_is_present()
    {
        // The control for the control: the mutation is what makes the difference, not the message.
        using var host = await Builder(withRepository: true).StartAsync();

        var result = await host.Services.GetRequiredService<IMessageBus>()
            .InvokeAsync<MPCore.Application.Results.Result<Fserp.FleetOperations.Modules.Fleet.Application.Views.VehicleView>>(
                new StartMaintenance(Guid.CreateVersion7()));

        // The chain was built and ran: the vehicle does not exist, so the handler answered 404 as a value
        // rather than anything throwing.
        output.WriteLine($"send StartMaintenance with IVehicleRepository: {result.FailureDescriptor?.Identity.Code ?? "success"}");
        Assert.True(result.IsFailure);
        Assert.Equal("VEHICLE_NOT_FOUND", result.FailureDescriptor!.Identity.Code);
    }

    [Fact]
    public async Task A_message_type_nobody_handles_fails_differently_so_the_two_are_distinguishable()
    {
        // The discriminator itself, measured: an unrouted message and an unresolvable port must not look
        // alike, or the assertion above would prove nothing.
        using var host = await Builder(withRepository: true).StartAsync();

        var sending = await Record.ExceptionAsync(() =>
            host.Services.GetRequiredService<IMessageBus>().InvokeAsync(new UnroutedProbe(Guid.NewGuid())));

        Report("send an unhandled message type", sending);
        Assert.NotNull(sending);
        Assert.DoesNotContain(
            Chain(sending),
            link => link.Message.Contains(nameof(IVehicleRepository), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Every_Fleet_command_and_query_chain_resolves_its_ports_and_runs()
    {
        // What the unverified item actually asked for. CacheEvictionRoutingTests sends the six events and
        // so builds only those six chains; these four were never invoked, and the experiment above shows
        // that a start would not have built them. Each is sent here, through a real runtime, so the whole
        // Fleet message surface is covered by a run rather than by an argument.
        using var host = await Builder(withRepository: true).StartAsync();
        var bus = host.Services.GetRequiredService<IMessageBus>();
        var unknown = Guid.CreateVersion7();

        var startMaintenance = await bus.InvokeAsync<Result<VehicleView>>(new StartMaintenance(unknown));
        var completeMaintenance = await bus.InvokeAsync<Result<VehicleView>>(new CompleteMaintenance(unknown));
        var changeStatus = await bus.InvokeAsync<Result<VehicleView>>(
            new ChangeVehicleStatus(unknown, Fserp.FleetOperations.Modules.Fleet.Domain.OperationalStatus.Inactive));
        var register = await bus.InvokeAsync<Result<VehicleView>>(
            new RegisterVehicle("AB-99", Fserp.FleetOperations.Modules.Fleet.Contracts.VehicleType.Van, 100m));
        var getVehicle = await bus.InvokeAsync<Result<VehicleView>>(new GetVehicle(unknown));
        // This one additionally resolves IReadThroughCache and IOptions<FleetCacheOptions>.
        var available = await bus.InvokeAsync<IReadOnlyList<AvailableVehicleView>>(new GetAvailableVehicles());

        Assert.Equal("VEHICLE_NOT_FOUND", startMaintenance.FailureDescriptor!.Identity.Code);
        Assert.Equal("VEHICLE_NOT_FOUND", completeMaintenance.FailureDescriptor!.Identity.Code);
        Assert.Equal("VEHICLE_NOT_FOUND", changeStatus.FailureDescriptor!.Identity.Code);
        Assert.True(register.IsSuccess);
        Assert.Equal("VEHICLE_NOT_FOUND", getVehicle.FailureDescriptor!.Identity.Code);
        Assert.NotNull(available);
    }

    [Fact]
    public async Task The_eviction_handler_still_runs_on_the_full_container()
    {
        // The fixture CacheEvictionRoutingTests composes is unchanged by this experiment.
        var cache = new FakeCache();
        using var host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseEnvironment(Environments.Production)
            .ConfigureServices(services =>
            {
                services.AddSingleton<ICache>(cache);
                services.AddSingleton<IReadThroughCache>(new FakeReadThroughCache());
                services.AddSingleton<IVehicleRepository, FakeVehicleRepository>();
                services.AddSingleton<IVehicleReadModel>(new EmptyVehicleReadModel());
                services.AddSingleton<IUnitOfWork, FakeUnitOfWork>();
                services.AddSingleton<IBusinessAuditRecorder, FakeAuditRecorder>();
                services.AddSingleton<IClock>(new FixedClock(DateTimeOffset.UnixEpoch));
                services.AddOptions<FleetCacheOptions>();
            })
            .UseWolverine(options =>
            {
                options.Durability.Mode = DurabilityMode.MediatorOnly;
                options.Discovery.IncludeAssembly(Fserp.FleetOperations.Modules.Fleet.AssemblyReference.Assembly);
            })
            .StartAsync();

        await host.Services.GetRequiredService<IMessageBus>()
            .InvokeAsync(new VehicleCommittedToMission(Guid.CreateVersion7(), Guid.CreateVersion7()));

        Assert.Equal(FleetCacheKeys.AvailableVehicles, Assert.Single(cache.Removed));
    }
}

/// <summary>A message type no handler in any discovered assembly handles.</summary>
public sealed record UnroutedProbe(Guid Id);

/// <summary>
/// A read model of an empty fleet. It answers every method rather than refusing the ones a narrower fake
/// does not serve, because this experiment's point is to run <em>every</em> chain to completion.
/// </summary>
internal sealed class EmptyVehicleReadModel : IVehicleReadModel
{
    public Task<VehicleView?> GetAsync(Guid vehicleId, CancellationToken cancellationToken) =>
        Task.FromResult<VehicleView?>(null);

    public Task<IReadOnlyList<AvailableVehicleView>> GetAvailableAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AvailableVehicleView>>([]);

    public Task<Fserp.FleetOperations.Modules.Fleet.Contracts.VehicleSnapshot?> GetSnapshotAsync(
        Guid vehicleId,
        CancellationToken cancellationToken) =>
        Task.FromResult<Fserp.FleetOperations.Modules.Fleet.Contracts.VehicleSnapshot?>(null);
}
