using Fserp.FleetOperations.Modules.Operations.Domain;

namespace Fserp.FleetOperations.Modules.Operations.Application;

/// <summary>
/// The names the Operations module records business actions under (docs/plans/operations.md, "Business
/// audit"). The actor is never passed: the recorder takes it from the validated token.
/// </summary>
public static class OperationsAudit
{
    /// <summary>The audit module name.</summary>
    public const string Module = "Operations";

    /// <summary>The audited entity type.</summary>
    public const string MissionEntity = nameof(Mission);

    /// <summary>A mission was created (required action).</summary>
    public const string MissionCreated = nameof(MissionCreated);

    /// <summary>A mission was scheduled (recommended action); also the action of its rejected attempt.</summary>
    public const string MissionScheduled = nameof(MissionScheduled);

    /// <summary>
    /// A vehicle and a driver were assigned (required action); also the action of every rejected attempt,
    /// which is the challenge's "a rejected operation is audited" proof.
    /// </summary>
    public const string MissionAssigned = nameof(MissionAssigned);

    /// <summary>A mission started (required action); also the action of its rejected attempt.</summary>
    public const string MissionStarted = nameof(MissionStarted);

    /// <summary>A mission completed (required action); also the action of its rejected attempt.</summary>
    public const string MissionCompleted = nameof(MissionCompleted);

    /// <summary>A mission was cancelled (required action); also the action of its rejected attempt.</summary>
    public const string MissionCancelled = nameof(MissionCancelled);

    /// <summary>Metadata key: the status before the transition.</summary>
    public const string FromMetadata = "from";

    /// <summary>Metadata key: the status after the transition.</summary>
    public const string ToMetadata = "to";

    /// <summary>Metadata key: the vehicle an assignment named.</summary>
    public const string VehicleIdMetadata = "vehicleId";

    /// <summary>Metadata key: the driver an assignment named.</summary>
    public const string DriverIdMetadata = "driverId";

    /// <summary>The <c>from</c> and <c>to</c> metadata of a transition.</summary>
    /// <param name="from">The status before the transition.</param>
    /// <param name="to">The status after the transition.</param>
    public static IReadOnlyDictionary<string, string> Transition(MissionStatus from, MissionStatus to) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [FromMetadata] = from.ToString(),
            [ToMetadata] = to.ToString(),
        };

    /// <summary>The vehicle and driver metadata of an assignment, successful or refused.</summary>
    /// <param name="vehicleId">The vehicle the assignment named.</param>
    /// <param name="driverId">The driver the assignment named.</param>
    public static IReadOnlyDictionary<string, string> Assignment(Guid vehicleId, Guid driverId) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [VehicleIdMetadata] = vehicleId.ToString(),
            [DriverIdMetadata] = driverId.ToString(),
        };
}
