using Fserp.FleetOperations.Modules.Fleet.Application;
using Fserp.FleetOperations.Modules.Fleet.Infrastructure.Persistence;
using Fserp.FleetOperations.Modules.Operations.Infrastructure.Persistence;
using MPCore.Application.Results;
using MPCore.Transport.Http;
using Npgsql;

namespace Fserp.FleetOperations.Api.Hosting;

/// <summary>
/// Turns a PostgreSQL unique violation on a known index into the failure the owning module declared,
/// instead of MP Core's generic <c>500</c>. It covers the race a handler's pre-check cannot close: two
/// requests both find the plate free, and the unique index refuses the second at commit, after the
/// handler has returned. Nothing of that request was committed.
/// </summary>
/// <remarks>
/// Only indexes listed in <see cref="UniqueViolations"/> are mapped; any other unique violation stays
/// unmapped and remains a <c>500</c>, because it would be a defect rather than an expected outcome.
/// Evaluated before the MP Core default mapper.
/// </remarks>
public sealed class UniqueViolationExceptionMapper : IHttpExceptionMapper
{
    /// <inheritdoc />
    public FailureDescriptor? Map(Exception exception, HttpContext context) => UniqueViolations.TryMap(exception);
}

/// <summary>The unique indexes whose violation is an expected outcome, and the failure each one means.</summary>
public static class UniqueViolations
{
    private static readonly Dictionary<string, Func<FailureDescriptor>> Known = new(StringComparer.Ordinal)
    {
        // F-2: plate numbers are unique across the fleet.
        [VehicleConfiguration.PlateNumberUniqueIndex] = FleetFailures.PlateNumberAlreadyRegistered,
        // Decision 2, O-9 (round 7): the two unique partial indexes on operations.missions. A violation
        // means another transaction committed an active mission holding this vehicle or this driver
        // between the moment this request read it as free and the moment it tried to commit — which is a
        // lost race, exactly what DbUpdateConcurrencyException means for the same assignment. Both
        // therefore answer the one host-wide 409, so a caller branches on one identity for one event
        // whichever mechanism caught it, and neither is a 500.
        [MissionConfiguration.ActiveVehicleUniqueIndex] = HostFailures.LostConcurrency,
        [MissionConfiguration.ActiveDriverUniqueIndex] = HostFailures.LostConcurrency,
    };

    /// <summary>
    /// The failure for a unique violation on a known index anywhere in the exception chain, or
    /// <see langword="null"/>.
    /// </summary>
    /// <param name="exception">The exception that escaped the request.</param>
    public static FailureDescriptor? TryMap(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: { } index }
                && Known.TryGetValue(index, out var failure))
            {
                return failure();
            }
        }

        return null;
    }
}
