namespace Fserp.FleetOperations.Modules.Drivers.Domain;

/// <summary>
/// The Drivers module's error domain and its stable codes. A code's message key is
/// <c>drivers.&lt;code in lower snake&gt;</c> (docs/plans/README.md); every key has a text in
/// <c>Resources/DriversMessages.resx</c>.
/// </summary>
public static class DriversErrors
{
    /// <summary>The error domain every Drivers failure is reported under.</summary>
    public const string Domain = "drivers";

    /// <summary>The full name is empty after normalization (D-1).</summary>
    public const string NameRequired = "DRIVER_NAME_REQUIRED";

    /// <summary>The driver was registered without a qualification (D-5).</summary>
    public const string QualificationRequired = "DRIVER_QUALIFICATION_REQUIRED";

    /// <summary>The same vehicle type was given twice at registration.</summary>
    public const string QualificationDuplicate = "DRIVER_QUALIFICATION_DUPLICATE";

    /// <summary>The driver is committed to a mission, so it cannot be set Inactive (D-2).</summary>
    public const string HasMissionCommitment = "DRIVER_HAS_MISSION_COMMITMENT";

    /// <summary>The driver's operational status is not Active (assignment precondition 6).</summary>
    public const string NotActive = "DRIVER_NOT_ACTIVE";

    /// <summary>The driver is committed to another mission (assignment precondition 7).</summary>
    public const string NotAvailable = "DRIVER_NOT_AVAILABLE";

    /// <summary>The driver holds no qualification for the vehicle type given (assignment precondition 8).</summary>
    public const string NotQualified = "DRIVER_NOT_QUALIFIED";

    /// <summary>The driver is not committed to the mission named, so it cannot be released from it.</summary>
    public const string NotCommittedToMission = "DRIVER_NOT_COMMITTED_TO_MISSION";

    /// <summary>No driver has the requested identity.</summary>
    public const string NotFound = "DRIVER_NOT_FOUND";

    /// <summary>The message key of a code: <c>drivers.</c> and the code in lower snake case.</summary>
    /// <param name="code">An UPPER_SNAKE code of this module.</param>
    public static string MessageKey(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return Domain + "." + code.ToLowerInvariant();
    }
}
