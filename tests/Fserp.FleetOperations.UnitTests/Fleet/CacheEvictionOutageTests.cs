using System.Reflection;
using Fserp.FleetOperations.Modules.Fleet.Application;
using Fserp.FleetOperations.Modules.Fleet.Application.Events;
using Fserp.FleetOperations.Modules.Fleet.Domain;
using Fserp.FleetOperations.Modules.Fleet.Domain.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MPCore.Caching.Abstractions;

namespace Fserp.FleetOperations.UnitTests.Fleet;

/// <summary>One call to <see cref="ILogger.Log{TState}"/>, kept whole: level, message, exception and the structured state.</summary>
internal sealed record LogLine(
    LogLevel Level,
    string Message,
    Exception? Exception,
    IReadOnlyList<KeyValuePair<string, object?>> State)
{
    /// <summary>The value of a structured property, or <see langword="null"/> when the template never named it.</summary>
    public object? Property(string name) =>
        State.FirstOrDefault(pair => pair.Key == name).Value;
}

/// <summary>
/// An <see cref="ILogger"/> that keeps every record whole. The state is read as the
/// <c>IReadOnlyList&lt;KeyValuePair&lt;string, object?&gt;&gt;</c> every <c>Log*</c> extension builds from a
/// message template, so a test can assert on the <em>property</em> a structured log carries rather than on
/// the sentence it renders.
/// </summary>
internal sealed class RecordingLogger : ILogger
{
    public List<LogLine> Lines { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Lines.Add(new LogLine(
            logLevel,
            formatter(state, exception),
            exception,
            state as IReadOnlyList<KeyValuePair<string, object?>> ?? []));
    }
}

/// <summary>An <see cref="ICache"/> whose removal fails, the way a level-2 outage reaches the handler.</summary>
/// <param name="failure">The exception the removal produces.</param>
/// <param name="synchronously">
/// <see langword="true"/> to throw before returning a task; <see langword="false"/> (the default, and the
/// shape a real async adapter produces) to return a faulted task. The second shape is the one that proves
/// the handler <em>awaits</em> inside its try: a try around a call that is only returned would not catch it.
/// </param>
internal sealed class FailingCache(Exception failure, bool synchronously = false) : ICache
{
    public List<string> Attempted { get; } = [];

    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult<T?>(default);

    public Task SetAsync<T>(string key, T value, TimeSpan? absoluteExpiration = null, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        Attempted.Add(key);
        return synchronously ? throw failure : Task.FromException(failure);
    }
}

/// <summary>
/// <c>AvailableVehiclesCacheEvictionHandler</c> during a cache outage (docs/architecture.md, decision 4,
/// as amended): the removal fails, the handler returns normally, and the failure leaves a warning carrying
/// the key and the exception. The amendment exists because <c>DefaultHybridCache.RemoveAsync</c> does not
/// catch a level-2 failure, so the exception really does reach this handler.
/// </summary>
/// <remarks>
/// This needs no Redis and no PostgreSQL: the outage is injected at the <see cref="ICache"/> port, which is
/// the same seam the handler depends on. So the behaviour is measured on every machine the unit tests run
/// on, rather than designed and left to the integration suite that skips without containers.
/// </remarks>
public sealed class CacheEvictionOutageTests
{
    private static readonly Guid Vehicle = Guid.CreateVersion7();
    private static readonly Guid Mission = Guid.CreateVersion7();

    /// <summary>The six events docs/plans/fleet.md lists; all six share the one eviction path.</summary>
    public static TheoryData<object> EveryDomainEvent() =>
    [
        new VehicleRegistered(Vehicle),
        new VehicleStatusChanged(Vehicle, OperationalStatus.Active, OperationalStatus.Inactive),
        new MaintenanceStarted(Vehicle),
        new MaintenanceCompleted(Vehicle),
        new VehicleCommittedToMission(Vehicle, Mission),
        new VehicleReleasedFromMission(Vehicle, Mission),
    ];

    /// <summary>Invokes the one <c>Handle</c> overload whose first parameter is this event's type.</summary>
    private static Task Invoke(object domainEvent, ICache cache, ILogger logger, CancellationToken cancellationToken)
    {
        var handle = Assert.Single(
            typeof(AvailableVehiclesCacheEvictionHandler).GetMethods(BindingFlags.Public | BindingFlags.Static),
            method => method.Name == "Handle" && method.GetParameters()[0].ParameterType == domainEvent.GetType());

        return (Task)handle.Invoke(null, [domainEvent, cache, logger, cancellationToken])!;
    }

    [Theory]
    [MemberData(nameof(EveryDomainEvent))]
    public async Task A_failing_removal_does_not_fail_the_handler_and_logs_the_key_and_the_exception(object domainEvent)
    {
        var outage = new InvalidOperationException("The level-2 cache is unreachable.");
        var cache = new FailingCache(outage);
        var logger = new RecordingLogger();

        // Returns normally: no exception reaches Wolverine, so the event is not retried or dead-lettered.
        var thrown = await Record.ExceptionAsync(() => Invoke(domainEvent, cache, logger, CancellationToken.None));

        Assert.Null(thrown);
        Assert.Equal(FleetCacheKeys.AvailableVehicles, Assert.Single(cache.Attempted));
        var line = Assert.Single(logger.Lines);
        Assert.Equal(LogLevel.Warning, line.Level);
        // The key as a structured property, not a sentence that happens to contain it, and the exception
        // on the record itself. A handler that logged "eviction failed" and nothing else would fail here.
        Assert.Equal(FleetCacheKeys.AvailableVehicles, line.Property("CacheKey"));
        Assert.Same(outage, line.Exception);
    }

    [Fact]
    public async Task A_removal_that_throws_before_returning_its_task_is_caught_the_same_way()
    {
        // The other shape an adapter can fail in. Both are caught, so the fix does not depend on which.
        var outage = new InvalidOperationException("The level-2 cache is unreachable.");
        var logger = new RecordingLogger();

        await AvailableVehiclesCacheEvictionHandler.Handle(
            new MaintenanceStarted(Vehicle), new FailingCache(outage, synchronously: true), logger, CancellationToken.None);

        var line = Assert.Single(logger.Lines);
        Assert.Equal(FleetCacheKeys.AvailableVehicles, line.Property("CacheKey"));
        Assert.Same(outage, line.Exception);
    }

    [Theory]
    [MemberData(nameof(EveryDomainEvent))]
    public async Task Cancellation_asked_for_by_the_caller_is_not_swallowed(object domainEvent)
    {
        // The deliberate exclusion: the handler's catch has a `when` filter that lets an
        // OperationCanceledException through while the caller's own token is cancelled. A shutdown or an
        // abandoned request is not a cache outage, and must not be reported as one.
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var cache = new FailingCache(new OperationCanceledException(cancellation.Token));
        var logger = new RecordingLogger();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Invoke(domainEvent, cache, logger, cancellation.Token));

        Assert.Empty(logger.Lines);
    }

    [Fact]
    public async Task A_cancellation_the_caller_did_not_ask_for_is_an_outage_and_is_logged()
    {
        // The width of that filter, stated: a level-2 client's internal timeout surfaces as a
        // TaskCanceledException while this handler's token is still live. That is the outage, not a
        // shutdown, so it is logged like any other failure rather than failing the handler.
        var outage = new TaskCanceledException("The level-2 cache timed out.");
        var logger = new RecordingLogger();

        await AvailableVehiclesCacheEvictionHandler.Handle(
            new MaintenanceCompleted(Vehicle), new FailingCache(outage), logger, CancellationToken.None);

        var line = Assert.Single(logger.Lines);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Same(outage, line.Exception);
    }

    [Fact]
    public async Task A_missing_cache_port_still_throws_because_that_is_a_composition_bug()
    {
        // ArgumentNullException.ThrowIfNull stays outside the try. A handler that absorbed this would hide
        // a container that never supplied the port, and every eviction would silently do nothing.
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => AvailableVehiclesCacheEvictionHandler.Handle(
                new VehicleRegistered(Vehicle), null!, NullLogger.Instance, CancellationToken.None));
    }

    [Fact]
    public async Task A_successful_removal_logs_nothing()
    {
        // The control: the warning is the outage's doing, not something every eviction writes.
        var logger = new RecordingLogger();

        await AvailableVehiclesCacheEvictionHandler.Handle(
            new VehicleRegistered(Vehicle), new FakeCache(), logger, CancellationToken.None);

        Assert.Empty(logger.Lines);
    }
}
