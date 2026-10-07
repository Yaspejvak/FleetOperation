using Microsoft.EntityFrameworkCore;
using MPCore.Application.Results;
using MPCore.Transport.Http;

namespace Fserp.FleetOperations.Api.Hosting;

/// <summary>
/// Turns a lost optimistic-concurrency race (<see cref="DbUpdateConcurrencyException"/>, raised at commit
/// when the <c>xmin</c>-checked UPDATE matches no row) into <c>409</c>
/// <c>fleetoperations/CONCURRENCY_CONFLICT</c> instead of MP Core's generic <c>500</c>
/// (docs/plans/fleet.md, "REST status codes"; docs/plans/README.md, "Failure mapping"). Nothing of that
/// request was committed.
/// </summary>
/// <remarks>
/// The exception is recognised by type anywhere in the inner-exception chain; its entries are not read,
/// so the mapping holds whichever aggregate lost the race and however the exception was wrapped.
/// Evaluated before the MP Core default mapper. Its gRPC twin, <see cref="ConcurrencyGrpcExceptionMapper"/>,
/// calls the same <see cref="TryMap"/>, so both transports answer the identical failure (F-10).
/// </remarks>
public sealed class ConcurrencyExceptionMapper : IHttpExceptionMapper
{
    /// <inheritdoc />
    public FailureDescriptor? Map(Exception exception, HttpContext context) => TryMap(exception);

    /// <summary>
    /// The lost-concurrency failure when a <see cref="DbUpdateConcurrencyException"/> is anywhere in the
    /// exception chain, or <see langword="null"/>.
    /// </summary>
    /// <param name="exception">The exception that escaped the request.</param>
    public static FailureDescriptor? TryMap(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is DbUpdateConcurrencyException)
            {
                return HostFailures.LostConcurrency();
            }
        }

        return null;
    }
}
