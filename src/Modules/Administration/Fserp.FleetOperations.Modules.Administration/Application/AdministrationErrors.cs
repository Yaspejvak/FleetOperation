namespace Fserp.FleetOperations.Modules.Administration.Application;

/// <summary>
/// The Administration module's error domain and its stable codes. A code's message key is
/// <c>administration.&lt;code in lower snake&gt;</c> (docs/plans/README.md); every key has a text in
/// <c>Resources/AdministrationMessages.resx</c>.
/// </summary>
/// <remarks>
/// This lives under <c>Application/</c>, not <c>Domain/</c> as the other modules' error classes do,
/// because docs/plans/administration.md says the <c>Domain/</c> folder stays empty: the module owns no
/// aggregate and no invariant. Every code here therefore belongs to a validator, never to a
/// <c>BusinessRule</c> — nothing in this module can break one.
/// </remarks>
public static class AdministrationErrors
{
    /// <summary>The error domain every Administration failure is reported under.</summary>
    public const string Domain = "administration";

    /// <summary>
    /// <c>from</c> is not strictly before <c>to</c>, though both were given. The plan names this check
    /// ("Validation: <c>from &lt; to</c> when both given"); the code and key are this module's.
    /// </summary>
    public const string AuditRangeInvalid = "AUDIT_RANGE_INVALID";

    /// <summary>The message key of a code: <c>administration.</c> and the code in lower snake case.</summary>
    /// <param name="code">An UPPER_SNAKE code of this module.</param>
    public static string MessageKey(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return Domain + "." + code.ToLowerInvariant();
    }
}
