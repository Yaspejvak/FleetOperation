namespace Fserp.FleetOperations.Modules.Fleet.Domain;

/// <summary>
/// The Fleet module's error domain and its stable codes. A code's message key is
/// <c>fleet.&lt;code in lower snake&gt;</c> (docs/plans/README.md); every key has a text in
/// <c>Resources/FleetMessages.resx</c>.
/// </summary>
public static class FleetErrors
{
    /// <summary>The error domain every Fleet failure is reported under.</summary>
    public const string Domain = "fleet";

    /// <summary>The plate number is empty after normalization.</summary>
    public const string PlateNumberRequired = "VEHICLE_PLATE_NUMBER_REQUIRED";

    /// <summary>The plate number is longer than 16 characters after normalization.</summary>
    public const string PlateNumberTooLong = "VEHICLE_PLATE_NUMBER_TOO_LONG";

    /// <summary>The capacity is zero or negative kilograms.</summary>
    public const string CapacityMustBePositive = "VEHICLE_CAPACITY_MUST_BE_POSITIVE";

    /// <summary>Another vehicle already carries the plate number (F-2).</summary>
    public const string PlateNumberAlreadyRegistered = "VEHICLE_PLATE_NUMBER_ALREADY_REGISTERED";

    /// <summary>The vehicle is committed to a mission (F-3, decision 3).</summary>
    public const string HasMissionCommitment = "VEHICLE_HAS_MISSION_COMMITMENT";

    /// <summary>The vehicle is already under maintenance, so maintenance cannot be started.</summary>
    public const string AlreadyUnderMaintenance = "VEHICLE_ALREADY_UNDER_MAINTENANCE";

    /// <summary>The vehicle is not under maintenance, so maintenance cannot be completed.</summary>
    public const string NotUnderMaintenance = "VEHICLE_NOT_UNDER_MAINTENANCE";

    /// <summary>The vehicle's operational status is not Active (assignment precondition 2).</summary>
    public const string NotActive = "VEHICLE_NOT_ACTIVE";

    /// <summary>The vehicle is under maintenance (assignment precondition 3).</summary>
    public const string UnderMaintenance = "VEHICLE_UNDER_MAINTENANCE";

    /// <summary>The vehicle's capacity is below the mission's required capacity (assignment precondition 4).</summary>
    public const string CapacityInsufficient = "VEHICLE_CAPACITY_INSUFFICIENT";

    /// <summary>The vehicle is committed to another mission (assignment precondition 5).</summary>
    public const string NotAvailable = "VEHICLE_NOT_AVAILABLE";

    /// <summary>The vehicle is not committed to the mission named, so it cannot be released from it.</summary>
    public const string NotCommittedToMission = "VEHICLE_NOT_COMMITTED_TO_MISSION";

    /// <summary>No vehicle has the requested identity.</summary>
    public const string NotFound = "VEHICLE_NOT_FOUND";

    /// <summary>The message key of a code: <c>fleet.</c> and the code in lower snake case.</summary>
    /// <param name="code">An UPPER_SNAKE code of this module.</param>
    public static string MessageKey(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return Domain + "." + code.ToLowerInvariant();
    }
}
