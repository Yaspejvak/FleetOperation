using System.Linq.Expressions;

namespace Fserp.FleetOperations.Modules.Drivers.Domain;

/// <summary>
/// What "available" means for a driver, written once (docs/plans/drivers.md):
/// <c>OperationalStatus == Active &amp;&amp; CommittedMissionId == null</c>.
/// </summary>
/// <remarks>
/// It is an expression rather than a method body so the one definition can be both translated to SQL by
/// the read model and evaluated in memory, instead of being written a second time for either. Fleet's
/// <c>VehicleAvailability</c> has the same shape for the same reason (L-9).
/// </remarks>
public static class DriverAvailability
{
    /// <summary>The availability predicate, as a query the read model can translate.</summary>
    public static Expression<Func<Driver, bool>> Specification { get; } = driver =>
        driver.OperationalStatus == OperationalStatus.Active
        && driver.CommittedMissionId == null;

    private static readonly Func<Driver, bool> Compiled = Specification.Compile();

    /// <summary>Whether a loaded driver is available, by the same definition.</summary>
    /// <param name="driver">The driver.</param>
    public static bool IsAvailable(Driver driver)
    {
        ArgumentNullException.ThrowIfNull(driver);
        return Compiled(driver);
    }
}
