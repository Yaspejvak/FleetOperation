using MPCore.Application.Results;

namespace Fserp.FleetOperations.Api.Hosting;

/// <summary>
/// Failures that belong to the host rather than to one module. A lost optimistic-concurrency race can
/// happen on any module's aggregate (a vehicle now, a driver and a mission later), so its failure is not
/// reported under a module's error domain. Every key has a text in <c>Resources/HostMessages.resx</c>.
/// </summary>
/// <remarks>
/// Decided (lead), round 1 closure: MP Core 0.9.3 offers <see cref="ErrorCategory.Concurrency"/> but no
/// descriptor or code for a lost race. The exact code remains O-9 (round 7); if MP Core is found to
/// produce its own, this one is replaced then.
/// </remarks>
public static class HostFailures
{
    /// <summary>The error domain of host-wide failures.</summary>
    public const string Domain = "fleetoperations";

    /// <summary>The row was changed by another request between read and commit.</summary>
    public const string ConcurrencyConflict = "CONCURRENCY_CONFLICT";

    /// <summary>The message key of a code: <c>fleetoperations.</c> and the code in lower snake case.</summary>
    /// <param name="code">An UPPER_SNAKE code of the host.</param>
    public static string MessageKey(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return Domain + "." + code.ToLowerInvariant();
    }

    /// <summary><c>409</c>: the operation lost an optimistic-concurrency check at commit.</summary>
    public static FailureDescriptor LostConcurrency() =>
        new(
            new ErrorIdentity(Domain, ConcurrencyConflict),
            ErrorCategory.Concurrency,
            new FailureMessageDescriptor(MessageKey(ConcurrencyConflict), null),
            null,
            null);
}
