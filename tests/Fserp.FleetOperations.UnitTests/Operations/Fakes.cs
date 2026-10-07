using Fserp.FleetOperations.Modules.Drivers.Contracts;
using Fserp.FleetOperations.Modules.Fleet.Contracts;
using Fserp.FleetOperations.Modules.Operations.Application.Ports;
using Fserp.FleetOperations.Modules.Operations.Application.Views;
using Fserp.FleetOperations.Modules.Operations.Domain;
using MPCore.Application.Querying;
using MPCore.Application.Results;
using MPCore.Domain.Rules;

namespace Fserp.FleetOperations.UnitTests.Operations;

/// <summary>
/// The sequence of port calls one handler made, in order. <c>AssignMission</c>'s step order is a
/// correctness property (docs/plans/operations.md, decision 2), so it is recorded and asserted rather
/// than read off the handler.
/// </summary>
internal sealed class PortCallLog
{
    private readonly List<string> _calls = [];

    public IReadOnlyList<string> Calls => _calls;

    public void Record(string call) => _calls.Add(call);
}

/// <summary>An in-memory <see cref="IMissionRepository"/>. It never saves: the middleware owns the unit of work.</summary>
internal sealed class FakeMissionRepository(PortCallLog? log = null) : IMissionRepository
{
    public const string LoadCall = "missions.Get";

    public Dictionary<Guid, Mission> Stored { get; } = [];

    public List<Mission> Added { get; } = [];

    public int Loads { get; private set; }

    public Task<Mission?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Loads++;
        log?.Record(LoadCall);
        return Task.FromResult(Stored.GetValueOrDefault(id));
    }

    public void Add(Mission aggregate)
    {
        Added.Add(aggregate);
        Stored[aggregate.Id] = aggregate;
    }

    public void Remove(Mission aggregate) => Stored.Remove(aggregate.Id);

    public Mission Store(Mission mission)
    {
        Stored[mission.Id] = mission;
        return mission;
    }
}

/// <summary>An in-memory <see cref="IMissionReadModel"/> that counts its reads.</summary>
internal sealed class FakeMissionReadModel : IMissionReadModel
{
    public MissionView? View { get; set; }

    public List<MissionView> Active { get; } = [];

    public Guid? AskedFor { get; private set; }

    public PageRequest? AskedForPage { get; private set; }

    public int Reads { get; private set; }

    public int ActiveReads { get; private set; }

    public Task<MissionView?> GetAsync(Guid missionId, CancellationToken cancellationToken)
    {
        AskedFor = missionId;
        Reads++;
        return Task.FromResult(View);
    }

    public Task<Page<MissionView>> GetActiveAsync(PageRequest page, CancellationToken cancellationToken)
    {
        AskedForPage = page;
        ActiveReads++;
        var rows = Active.Skip(page.Skip).Take(page.Size).ToList();
        return Task.FromResult(new Page<MissionView>(rows, page.Number, page.Size, Active.Count));
    }
}

/// <summary>
/// A stand-in for Fleet's <see cref="IVehicleAvailabilityReader"/>. It answers with whatever the test put
/// in <see cref="Snapshot"/> and records that it was asked.
/// </summary>
internal sealed class FakeVehicleAvailabilityReader(PortCallLog? log = null) : IVehicleAvailabilityReader
{
    public const string ReadCall = "vehicleReader.Get";

    public VehicleSnapshot? Snapshot { get; set; }

    public Guid? AskedFor { get; private set; }

    public Task<VehicleSnapshot?> GetAsync(Guid vehicleId, CancellationToken cancellationToken)
    {
        AskedFor = vehicleId;
        log?.Record(ReadCall);
        return Task.FromResult(Snapshot);
    }
}

/// <summary>A stand-in for Drivers' <see cref="IDriverEligibilityReader"/>.</summary>
internal sealed class FakeDriverEligibilityReader(PortCallLog? log = null) : IDriverEligibilityReader
{
    public const string ReadCall = "driverReader.Get";

    public DriverSnapshot? Snapshot { get; set; }

    public Guid? AskedFor { get; private set; }

    public Task<DriverSnapshot?> GetAsync(Guid driverId, CancellationToken cancellationToken)
    {
        AskedFor = driverId;
        log?.Record(ReadCall);
        return Task.FromResult(Snapshot);
    }
}

/// <summary>
/// A stand-in for Fleet's writing Contracts port. It records every call and raises whatever the test
/// configured, so a refusal by the real <c>Vehicle</c> aggregate can be reproduced without a database.
/// </summary>
internal sealed class FakeVehicleCommitments(PortCallLog? log = null) : IVehicleCommitments
{
    public const string CommitCall = "vehicles.CommitToMission";
    public const string ReleaseCall = "vehicles.ReleaseFromMission";

    public List<(Guid VehicleId, Guid MissionId, decimal RequiredCapacityKg)> Commits { get; } = [];

    public List<(Guid VehicleId, Guid MissionId)> Releases { get; } = [];

    /// <summary>What the commitment raises, or <see langword="null"/> when it succeeds.</summary>
    public Func<Exception>? CommitThrows { get; set; }

    /// <summary>What the release raises, or <see langword="null"/> when it succeeds.</summary>
    public Func<Exception>? ReleaseThrows { get; set; }

    public Task CommitToMissionAsync(Guid vehicleId, Guid missionId, decimal requiredCapacityKg, CancellationToken cancellationToken)
    {
        log?.Record(CommitCall);
        Commits.Add((vehicleId, missionId, requiredCapacityKg));
        return CommitThrows is null ? Task.CompletedTask : Task.FromException(CommitThrows());
    }

    public Task ReleaseFromMissionAsync(Guid vehicleId, Guid missionId, CancellationToken cancellationToken)
    {
        log?.Record(ReleaseCall);
        Releases.Add((vehicleId, missionId));
        return ReleaseThrows is null ? Task.CompletedTask : Task.FromException(ReleaseThrows());
    }
}

/// <summary>A stand-in for Drivers' writing Contracts port.</summary>
internal sealed class FakeDriverCommitments(PortCallLog? log = null) : IDriverCommitments
{
    public const string CommitCall = "drivers.CommitToMission";
    public const string ReleaseCall = "drivers.ReleaseFromMission";

    public List<(Guid DriverId, Guid MissionId, VehicleType VehicleType)> Commits { get; } = [];

    public List<(Guid DriverId, Guid MissionId)> Releases { get; } = [];

    public Func<Exception>? CommitThrows { get; set; }

    public Func<Exception>? ReleaseThrows { get; set; }

    public Task CommitToMissionAsync(Guid driverId, Guid missionId, VehicleType vehicleType, CancellationToken cancellationToken)
    {
        log?.Record(CommitCall);
        Commits.Add((driverId, missionId, vehicleType));
        return CommitThrows is null ? Task.CompletedTask : Task.FromException(CommitThrows());
    }

    public Task ReleaseFromMissionAsync(Guid driverId, Guid missionId, CancellationToken cancellationToken)
    {
        log?.Record(ReleaseCall);
        Releases.Add((driverId, missionId));
        return ReleaseThrows is null ? Task.CompletedTask : Task.FromException(ReleaseThrows());
    }
}

/// <summary>
/// An audit recorder that also records the order of its calls into the shared log, so "the audit row is
/// written after the two commitments" is asserted rather than assumed.
/// </summary>
internal sealed class LoggingAuditRecorder(PortCallLog? log = null) : MPCore.Audit.IBusinessAuditRecorder
{
    public const string RecordCall = "audit.Record";
    public const string AttemptCall = "audit.RecordAttempt";

    public List<RecordedMissionAction> Actions { get; } = [];

    public List<RecordedMissionAttempt> Attempts { get; } = [];

    public ValueTask RecordAsync(
        string module,
        string action,
        string? entityType = null,
        string? entityId = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        log?.Record(RecordCall);
        Actions.Add(new RecordedMissionAction(module, action, entityType, entityId, metadata));
        return ValueTask.CompletedTask;
    }

    public ValueTask RecordAttemptAsync(
        string module,
        string action,
        MPCore.Audit.AuditOutcome outcome,
        MPCore.Audit.AuditFailure? failure = null,
        string? reason = null,
        string? entityType = null,
        string? entityId = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        log?.Record(AttemptCall);
        Attempts.Add(new RecordedMissionAttempt(module, action, outcome, failure, entityType, entityId, metadata));
        return ValueTask.CompletedTask;
    }
}

internal sealed record RecordedMissionAction(
    string Module,
    string Action,
    string? EntityType,
    string? EntityId,
    IReadOnlyDictionary<string, string>? Metadata);

internal sealed record RecordedMissionAttempt(
    string Module,
    string Action,
    MPCore.Audit.AuditOutcome Outcome,
    MPCore.Audit.AuditFailure? Failure,
    string? EntityType,
    string? EntityId,
    IReadOnlyDictionary<string, string>? Metadata);

internal sealed class FakeOperationsUnitOfWork : MPCore.Persistence.Abstractions.IUnitOfWork
{
    public int Saves { get; private set; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        Saves++;
        return Task.FromResult(0);
    }
}

internal sealed class FixedOperationsClock(DateTimeOffset now) : MPCore.Application.Time.IClock
{
    public DateTimeOffset UtcNow { get; } = now;
}

/// <summary>Missions in each state, built only through the aggregate's own transitions.</summary>
internal static class TestMissions
{
    public static readonly DateTimeOffset Now = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);

    public static readonly DateTimeOffset ScheduledAt = new(2026, 10, 6, 7, 30, 0, TimeSpan.Zero);

    public static Mission Draft(string origin = "Tehran", string destination = "Isfahan", decimal requiredCapacityKg = 800m)
    {
        var mission = Mission.Create(
            Guid.CreateVersion7(Now),
            Location.Create(origin),
            Location.Create(destination),
            RequiredCapacity.FromKilograms(requiredCapacityKg));
        mission.ClearEvents();
        return mission;
    }

    public static Mission Scheduled(decimal requiredCapacityKg = 800m)
    {
        var mission = Draft(requiredCapacityKg: requiredCapacityKg);
        mission.Schedule(ScheduledAt);
        mission.ClearEvents();
        return mission;
    }

    public static Mission Assigned(Guid vehicleId, Guid driverId, decimal requiredCapacityKg = 800m)
    {
        var mission = Scheduled(requiredCapacityKg);
        mission.Assign(vehicleId, driverId);
        mission.ClearEvents();
        return mission;
    }

    public static Mission InProgress(Guid vehicleId, Guid driverId, decimal requiredCapacityKg = 800m)
    {
        var mission = Assigned(vehicleId, driverId, requiredCapacityKg);
        mission.Start();
        mission.ClearEvents();
        return mission;
    }

    public static Mission Completed(Guid vehicleId, Guid driverId)
    {
        var mission = InProgress(vehicleId, driverId);
        mission.Complete();
        mission.ClearEvents();
        return mission;
    }

    public static Mission Cancelled()
    {
        var mission = Draft();
        mission.Cancel();
        mission.ClearEvents();
        return mission;
    }

    /// <summary>A mission in the given status, built only through legal transitions.</summary>
    public static Mission In(MissionStatus status, Guid? vehicleId = null, Guid? driverId = null)
    {
        var vehicle = vehicleId ?? Guid.CreateVersion7(Now);
        var driver = driverId ?? Guid.CreateVersion7(Now);
        return status switch
        {
            MissionStatus.Draft => Draft(),
            MissionStatus.Scheduled => Scheduled(),
            MissionStatus.Assigned => Assigned(vehicle, driver),
            MissionStatus.InProgress => InProgress(vehicle, driver),
            MissionStatus.Completed => Completed(vehicle, driver),
            MissionStatus.Cancelled => Cancelled(),
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Not a member of MissionStatus."),
        };
    }

    /// <summary>A vehicle snapshot as Fleet would report it: active, not under maintenance, free.</summary>
    public static VehicleSnapshot VehicleSnapshot(
        Guid vehicleId,
        VehicleType type = VehicleType.Truck,
        decimal capacityKg = 3000m) =>
        new(vehicleId, type, capacityKg, IsActive: true, IsUnderMaintenance: false, CommittedMissionId: null);

    /// <summary>A driver snapshot as Drivers would report it: active, qualified, free.</summary>
    public static DriverSnapshot DriverSnapshot(Guid driverId, params VehicleType[] types) =>
        new(driverId, IsActive: true, types.Length == 0 ? [VehicleType.Truck] : types, CommittedMissionId: null);

    /// <summary>The exception a rule check raises, captured so a fake port can raise the very same one.</summary>
    public static BusinessRuleValidationException Broken(IBusinessRule rule)
    {
        try
        {
            BusinessRules.Check(rule);
        }
        catch (BusinessRuleValidationException broken)
        {
            return broken;
        }

        throw new InvalidOperationException($"{rule.Code} is not broken by this state.");
    }

    /// <summary>The not-found failure a commitment port raises when its resource disappeared (L-23).</summary>
    public static ResultFailureException Missing(string domain, string code) =>
        new(new FailureDescriptor(
            new ErrorIdentity(domain, code),
            ErrorCategory.NotFound,
            new FailureMessageDescriptor(domain + "." + code.ToLowerInvariant(), null),
            null,
            null));
}
