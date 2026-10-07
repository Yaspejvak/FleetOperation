using Grpc.Core;
using MPCore.Application.Results;
using MPCore.Transport.Grpc;

namespace Fserp.FleetOperations.Api.Hosting;

/// <summary>
/// The gRPC twin of <see cref="UniqueViolationExceptionMapper"/> (F-10). It reuses
/// <see cref="UniqueViolations.TryMap"/>, the same static entry point the HTTP mapper calls, so the index
/// table is written once and a unique violation answers the same failure descriptor on both transports.
/// </summary>
/// <remarks>
/// MP Core 0.9.3 has no <c>AddGrpcExceptionMapper</c> extension (unlike <c>AddHttpExceptionMapper</c>);
/// the failure interceptor resolves <c>IEnumerable&lt;IGrpcExceptionMapper&gt;</c> from the container, so
/// this type is registered directly in <c>Program.cs</c>.
/// </remarks>
public sealed class UniqueViolationGrpcExceptionMapper : IGrpcExceptionMapper
{
    /// <inheritdoc />
    public FailureDescriptor? Map(Exception exception, ServerCallContext context) => UniqueViolations.TryMap(exception);
}

/// <summary>
/// The gRPC twin of <see cref="ConcurrencyExceptionMapper"/> (F-10). It reuses
/// <see cref="ConcurrencyExceptionMapper.TryMap"/>, so a lost <c>xmin</c> race answers
/// <c>fleetoperations/CONCURRENCY_CONFLICT</c> on both transports from one mapping.
/// </summary>
public sealed class ConcurrencyGrpcExceptionMapper : IGrpcExceptionMapper
{
    /// <inheritdoc />
    public FailureDescriptor? Map(Exception exception, ServerCallContext context) => ConcurrencyExceptionMapper.TryMap(exception);
}
