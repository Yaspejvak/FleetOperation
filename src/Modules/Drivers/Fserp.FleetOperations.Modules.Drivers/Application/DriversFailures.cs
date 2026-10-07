using Fserp.FleetOperations.Modules.Drivers.Domain;
using MPCore.Application.Results;

namespace Fserp.FleetOperations.Modules.Drivers.Application;

/// <summary>
/// The expected, non-rule outcomes of the Drivers use cases, returned as failure descriptors and mapped at
/// the edge (<c>404</c>). Broken business rules are not here: they travel as
/// <c>BusinessRuleValidationException</c> from the aggregate and map to <c>422</c>.
/// </summary>
public static class DriversFailures
{
    /// <summary><c>404</c>: no driver has the requested identity.</summary>
    public static FailureDescriptor DriverNotFound() =>
        Create(DriversErrors.NotFound, ErrorCategory.NotFound);

    private static FailureDescriptor Create(string code, ErrorCategory category) =>
        new(
            new ErrorIdentity(DriversErrors.Domain, code),
            category,
            new FailureMessageDescriptor(DriversErrors.MessageKey(code), null),
            null,
            null);
}
