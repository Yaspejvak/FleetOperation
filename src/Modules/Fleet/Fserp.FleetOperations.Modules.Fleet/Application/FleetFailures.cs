using Fserp.FleetOperations.Modules.Fleet.Domain;
using MPCore.Application.Results;

namespace Fserp.FleetOperations.Modules.Fleet.Application;

/// <summary>
/// The expected, non-rule outcomes of the Fleet use cases, returned as failure descriptors and mapped at
/// the edge (<c>404</c>, <c>409</c>). Broken business rules are not here: they travel as
/// <c>BusinessRuleValidationException</c> from the aggregate and map to <c>422</c>.
/// </summary>
public static class FleetFailures
{
    /// <summary><c>404</c>: no vehicle has the requested identity.</summary>
    public static FailureDescriptor VehicleNotFound() =>
        Create(FleetErrors.NotFound, ErrorCategory.NotFound);

    /// <summary><c>409</c>: another vehicle already carries this plate number (F-2).</summary>
    /// <remarks>
    /// Returned by Register Vehicle when the plate is already taken, and produced by the host's
    /// persistence exception mapper when two registrations race and the unique index refuses the second.
    /// </remarks>
    public static FailureDescriptor PlateNumberAlreadyRegistered() =>
        Create(FleetErrors.PlateNumberAlreadyRegistered, ErrorCategory.AlreadyExists);

    private static FailureDescriptor Create(string code, ErrorCategory category) =>
        new(
            new ErrorIdentity(FleetErrors.Domain, code),
            category,
            new FailureMessageDescriptor(FleetErrors.MessageKey(code), null),
            null,
            null);
}
