using Fserp.FleetOperations.Modules.Drivers.Application.Ports;
using Fserp.FleetOperations.Modules.Drivers.Application.Views;
using Fserp.FleetOperations.Modules.Drivers.Contracts;
using Fserp.FleetOperations.Modules.Drivers.Domain;
using Fserp.FleetOperations.Modules.Fleet.Contracts;

namespace Fserp.FleetOperations.UnitTests.Drivers;

/// <summary>
/// An in-memory <see cref="IDriverRepository"/>. It never saves: the middleware owns the unit of work, and
/// a handler that called <c>SaveChangesAsync</c> would be caught by the unit-of-work fake instead.
/// </summary>
internal sealed class FakeDriverRepository : IDriverRepository
{
    public Dictionary<Guid, Driver> Stored { get; } = [];

    public List<Driver> Added { get; } = [];

    public int Loads { get; private set; }

    public Task<Driver?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Loads++;
        return Task.FromResult(Stored.GetValueOrDefault(id));
    }

    public void Add(Driver aggregate)
    {
        Added.Add(aggregate);
        Stored[aggregate.Id] = aggregate;
    }

    public void Remove(Driver aggregate) => Stored.Remove(aggregate.Id);

    public Driver Store(Driver driver)
    {
        Stored[driver.Id] = driver;
        return driver;
    }
}

/// <summary>
/// An in-memory <see cref="IDriverReadModel"/> that counts its reads, so "this answer came from the
/// database and not from somewhere it was kept" can be asserted rather than assumed.
/// </summary>
internal sealed class FakeDriverReadModel : IDriverReadModel
{
    public DriverView? View { get; set; }

    public DriverSnapshot? Snapshot { get; set; }

    public List<AvailableDriverView> Available { get; } = [];

    public Guid? AskedFor { get; private set; }

    public int Reads { get; private set; }

    public int SnapshotReads { get; private set; }

    public int AvailableReads { get; private set; }

    public Task<DriverView?> GetAsync(Guid driverId, CancellationToken cancellationToken)
    {
        AskedFor = driverId;
        Reads++;
        return Task.FromResult(View);
    }

    public Task<IReadOnlyList<AvailableDriverView>> GetAvailableAsync(CancellationToken cancellationToken)
    {
        AvailableReads++;
        return Task.FromResult<IReadOnlyList<AvailableDriverView>>(Available);
    }

    public Task<DriverSnapshot?> GetSnapshotAsync(Guid driverId, CancellationToken cancellationToken)
    {
        AskedFor = driverId;
        SnapshotReads++;
        return Task.FromResult(Snapshot);
    }
}

internal static class TestDrivers
{
    public static readonly DateTimeOffset Now = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A registered driver, with its registration event cleared so a test asserts only its own.</summary>
    public static Driver Registered(string fullName = "Ada Lovelace", params VehicleType[] vehicleTypes)
    {
        var driver = Driver.Register(
            Guid.CreateVersion7(Now),
            DriverName.Create(fullName),
            vehicleTypes.Length == 0 ? [VehicleType.Truck] : vehicleTypes,
            Now);
        driver.ClearEvents();
        return driver;
    }

    /// <summary>
    /// A driver committed to a mission, through the aggregate's own method rather than a setter: the
    /// commitment path is production code from this round on.
    /// </summary>
    public static Driver CommittedTo(this Driver driver, Guid missionId, VehicleType vehicleType = VehicleType.Truck)
    {
        driver.CommitToMission(missionId, vehicleType);
        driver.ClearEvents();
        return driver;
    }
}
