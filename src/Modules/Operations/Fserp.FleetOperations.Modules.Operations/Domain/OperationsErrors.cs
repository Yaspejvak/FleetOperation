namespace Fserp.FleetOperations.Modules.Operations.Domain;

/// <summary>
/// The Operations module's error domain and its stable codes. A code's message key is
/// <c>operations.&lt;code in lower snake&gt;</c> (docs/plans/README.md); every key has a text in
/// <c>Resources/OperationsMessages.resx</c>.
/// </summary>
public static class OperationsErrors
{
    /// <summary>The error domain every Operations failure is reported under.</summary>
    public const string Domain = "operations";

    /// <summary>The required capacity is zero or negative kilograms (X-4).</summary>
    public const string RequiredCapacityMustBePositive = "MISSION_REQUIRED_CAPACITY_MUST_BE_POSITIVE";

    /// <summary>A location is empty after normalization (O-7).</summary>
    public const string LocationRequired = "MISSION_LOCATION_REQUIRED";

    /// <summary>The requested transition is not an edge of the state machine (docs/plans/operations.md).</summary>
    public const string InvalidTransition = "MISSION_INVALID_TRANSITION";

    /// <summary>The mission is not <c>Scheduled</c>, so it cannot be assigned (assignment precondition 1).</summary>
    public const string NotAssignable = "MISSION_NOT_ASSIGNABLE";

    /// <summary>No mission has the requested identity.</summary>
    public const string NotFound = "MISSION_NOT_FOUND";

    /// <summary>
    /// No vehicle has the identity the assignment named, as <c>IVehicleAvailabilityReader</c> reported it
    /// (AssignMission step 2).
    /// </summary>
    /// <remarks>
    /// Operations' own code rather than Fleet's <c>VEHICLE_NOT_FOUND</c>: Operations may not reference
    /// Fleet's main project, where that failure descriptor lives, and writing Fleet's domain and code into
    /// this module would be a copy of another module's vocabulary that nothing keeps in step. The narrow
    /// path of L-23 — the vehicle disappearing between step 2 and step 4 — still answers Fleet's own
    /// <c>fleet/VEHICLE_NOT_FOUND</c>, because Fleet raises it; both are <c>404</c>.
    /// </remarks>
    public const string VehicleNotFound = "MISSION_VEHICLE_NOT_FOUND";

    /// <summary>
    /// No driver has the identity the assignment named, as <c>IDriverEligibilityReader</c> reported it
    /// (AssignMission step 2). The counterpart of <see cref="VehicleNotFound"/>.
    /// </summary>
    public const string DriverNotFound = "MISSION_DRIVER_NOT_FOUND";

    /// <summary>The message key of a code: <c>operations.</c> and the code in lower snake case.</summary>
    /// <param name="code">An UPPER_SNAKE code of this module.</param>
    public static string MessageKey(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return Domain + "." + code.ToLowerInvariant();
    }
}
