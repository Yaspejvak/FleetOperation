using System.Reflection;
using Fserp.FleetOperations.Modules.Fleet.Application.Ports;
using Fserp.FleetOperations.Modules.Fleet.Contracts;
using Fserp.FleetOperations.Modules.Fleet.Domain;
using MPCore.Application.Time;
using MPCore.Audit;
using MPCore.Persistence.Abstractions;

namespace Fserp.FleetOperations.UnitTests.Fleet;

internal sealed class FakeVehicleRepository : IVehicleRepository
{
    public Dictionary<Guid, Vehicle> Stored { get; } = [];

    public List<Vehicle> Added { get; } = [];

    public Task<Vehicle?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Stored.GetValueOrDefault(id));

    public void Add(Vehicle aggregate)
    {
        Added.Add(aggregate);
        Stored[aggregate.Id] = aggregate;
    }

    public void Remove(Vehicle aggregate) => Stored.Remove(aggregate.Id);

    public Task<bool> PlateNumberExistsAsync(PlateNumber plateNumber, CancellationToken cancellationToken) =>
        Task.FromResult(Stored.Values.Any(vehicle => vehicle.PlateNumber.Equals(plateNumber)));
}

internal sealed record RecordedAction(
    string Module,
    string Action,
    string? EntityType,
    string? EntityId,
    IReadOnlyDictionary<string, string>? Metadata);

internal sealed record RecordedAttempt(
    string Module,
    string Action,
    AuditOutcome Outcome,
    AuditFailure? Failure,
    string? EntityType,
    string? EntityId,
    IReadOnlyDictionary<string, string>? Metadata);

internal sealed class FakeAuditRecorder : IBusinessAuditRecorder
{
    public List<RecordedAction> Actions { get; } = [];

    public List<RecordedAttempt> Attempts { get; } = [];

    public ValueTask RecordAsync(
        string module,
        string action,
        string? entityType = null,
        string? entityId = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        Actions.Add(new RecordedAction(module, action, entityType, entityId, metadata));
        return ValueTask.CompletedTask;
    }

    public ValueTask RecordAttemptAsync(
        string module,
        string action,
        AuditOutcome outcome,
        AuditFailure? failure = null,
        string? reason = null,
        string? entityType = null,
        string? entityId = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        Attempts.Add(new RecordedAttempt(module, action, outcome, failure, entityType, entityId, metadata));
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeUnitOfWork : IUnitOfWork
{
    public int Saves { get; private set; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        Saves++;
        return Task.FromResult(0);
    }
}

internal sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; } = now;
}

internal static class TestVehicles
{
    public static Vehicle Registered(string plate = "AB-123", decimal capacityKg = 1000m, VehicleType type = VehicleType.Truck)
    {
        var vehicle = Vehicle.Register(Guid.CreateVersion7(), PlateNumber.Create(plate), type, Capacity.FromKilograms(capacityKg));
        vehicle.ClearEvents();
        return vehicle;
    }

    /// <summary>
    /// Puts a vehicle in the committed state. No production path sets the commitment in round 1 (the
    /// commitment port arrives later), so the test reaches the private setter; the rule under test is
    /// the aggregate's reaction to that state, not how the state is reached.
    /// </summary>
    public static Vehicle CommittedTo(this Vehicle vehicle, Guid missionId)
    {
        typeof(Vehicle)
            .GetProperty(nameof(Vehicle.CommittedMissionId), BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(vehicle, missionId);
        return vehicle;
    }
}
