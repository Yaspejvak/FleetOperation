using System.Linq.Expressions;

namespace Fserp.FleetOperations.Modules.Operations.Domain;

/// <summary>
/// The two status sets the module reasons about, written once so a query and an index predicate cannot
/// disagree about them.
/// </summary>
/// <remarks>
/// They are deliberately different sets. <see cref="ActiveStatuses"/> is what a reader means by "active"
/// (O-4) and includes <see cref="MissionStatus.Scheduled"/>, which holds no resources yet.
/// <see cref="ResourceHoldingStatuses"/> is what "conflicting" means (O-8): the statuses in which a mission
/// holds a vehicle and a driver, and therefore the predicate of the two unique partial indexes.
/// </remarks>
public static class MissionActivity
{
    /// <summary>Active for a reader: Scheduled, Assigned, InProgress (O-4).</summary>
    public static IReadOnlyList<MissionStatus> ActiveStatuses { get; } =
        [MissionStatus.Scheduled, MissionStatus.Assigned, MissionStatus.InProgress];

    /// <summary>
    /// The statuses in which a mission holds a vehicle and a driver: Assigned, InProgress (O-8). The
    /// predicate of <c>ux_missions_active_vehicle</c> and <c>ux_missions_active_driver</c>.
    /// </summary>
    public static IReadOnlyList<MissionStatus> ResourceHoldingStatuses { get; } =
        [MissionStatus.Assigned, MissionStatus.InProgress];

    /// <summary>The active predicate, as a query the read model can translate to SQL.</summary>
    public static Expression<Func<Mission, bool>> ActiveSpecification { get; } = mission =>
        mission.Status == MissionStatus.Scheduled
        || mission.Status == MissionStatus.Assigned
        || mission.Status == MissionStatus.InProgress;

    private static readonly Func<Mission, bool> CompiledActive = ActiveSpecification.Compile();

    /// <summary>Whether a loaded mission is active, by the same definition.</summary>
    /// <param name="mission">The mission.</param>
    public static bool IsActive(Mission mission)
    {
        ArgumentNullException.ThrowIfNull(mission);
        return CompiledActive(mission);
    }
}
