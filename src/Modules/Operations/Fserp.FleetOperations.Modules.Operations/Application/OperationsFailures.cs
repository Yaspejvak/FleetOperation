using Fserp.FleetOperations.Modules.Operations.Domain;
using MPCore.Application.Results;

namespace Fserp.FleetOperations.Modules.Operations.Application;

/// <summary>
/// The expected, non-rule outcomes of the Operations use cases, returned as failure descriptors and mapped
/// at the edge (<c>404</c>). Broken business rules are not here: they travel as
/// <c>BusinessRuleValidationException</c> from the aggregate and map to <c>422</c>, and a lost race at
/// commit is the host-wide <c>409</c>.
/// </summary>
public static class OperationsFailures
{
    /// <summary><c>404</c>: no mission has the requested identity.</summary>
    public static FailureDescriptor MissionNotFound() =>
        Create(OperationsErrors.NotFound, ErrorCategory.NotFound);

    /// <summary><c>404</c>: no vehicle has the identity the assignment named (AssignMission step 2).</summary>
    public static FailureDescriptor VehicleNotFound() =>
        Create(OperationsErrors.VehicleNotFound, ErrorCategory.NotFound);

    /// <summary><c>404</c>: no driver has the identity the assignment named (AssignMission step 2).</summary>
    public static FailureDescriptor DriverNotFound() =>
        Create(OperationsErrors.DriverNotFound, ErrorCategory.NotFound);

    private static FailureDescriptor Create(string code, ErrorCategory category) =>
        new(
            new ErrorIdentity(OperationsErrors.Domain, code),
            category,
            new FailureMessageDescriptor(OperationsErrors.MessageKey(code), null),
            null,
            null);
}
