using Fserp.FleetOperations.Api.Hosting;
using Fserp.FleetOperations.Modules.Operations.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using MPCore.Application.Results;
using Npgsql;

namespace Fserp.FleetOperations.UnitTests.Host;

/// <summary>
/// O-9, at the mapping level: which failure this host produces for the two ways an assignment can lose a
/// race at commit — the <c>xmin</c> check failing, and a violation of one of the two unique partial
/// indexes on <c>operations.missions</c>.
/// </summary>
/// <remarks>
/// <para>
/// What these tests establish without a database: both ways answer the one host-wide
/// <c>409 fleetoperations/CONCURRENCY_CONFLICT</c> (<see cref="ErrorCategory.Concurrency"/>), on both
/// transports, from one table. What they cannot establish is that PostgreSQL actually raises either
/// exception for two real concurrent assignments; that is
/// <c>ConcurrentAssignmentTests</c> in the integration suite, which needs a running database.
/// </para>
/// <para>
/// MP Core 0.9.3 ships no mapper of its own for <see cref="DbUpdateConcurrencyException"/> or for a
/// PostgreSQL unique violation: without this repository's two <c>IHttpExceptionMapper</c>s and their gRPC
/// twins, both would reach the caller as a generic <c>500</c>. The <c>409</c> comes from the category on
/// the descriptor those mappers return, not from the framework recognising the exception.
/// </para>
/// </remarks>
public sealed class OperationsConcurrencyMappingTests
{
    private static DbUpdateException UniqueViolationOn(string index) =>
        new(
            "save failed",
            new PostgresException(
                "duplicate key value violates unique constraint",
                "ERROR",
                "ERROR",
                PostgresErrorCodes.UniqueViolation,
                constraintName: index));

    [Theory]
    [InlineData("ux_missions_active_vehicle")]
    [InlineData("ux_missions_active_driver")]
    public void A_violation_of_either_partial_index_is_a_mapped_409_and_not_an_unmapped_500(string index)
    {
        var failure = UniqueViolations.TryMap(UniqueViolationOn(index));

        Assert.NotNull(failure);
        Assert.Equal(new ErrorIdentity("fleetoperations", "CONCURRENCY_CONFLICT"), failure.Identity);
        Assert.Equal(ErrorCategory.Concurrency, failure.Category);
    }

    [Fact]
    public void The_two_index_names_in_the_table_are_the_ones_the_mapping_declares()
    {
        // The table is keyed by the constant the EF mapping uses, so renaming an index in one place
        // without the other cannot compile.
        Assert.NotNull(UniqueViolations.TryMap(UniqueViolationOn(MissionConfiguration.ActiveVehicleUniqueIndex)));
        Assert.NotNull(UniqueViolations.TryMap(UniqueViolationOn(MissionConfiguration.ActiveDriverUniqueIndex)));
    }

    [Fact]
    public void A_lost_xmin_race_answers_the_identical_failure()
    {
        // The two mechanisms of decision 2 answer one identity, so a caller branches on one code for one
        // event whichever of them caught it.
        var index = UniqueViolations.TryMap(UniqueViolationOn(MissionConfiguration.ActiveVehicleUniqueIndex));
        var token = ConcurrencyExceptionMapper.TryMap(new DbUpdateConcurrencyException("The row was changed."));

        Assert.NotNull(index);
        Assert.NotNull(token);
        Assert.Equal(token.Identity, index.Identity);
        Assert.Equal(token.Category, index.Category);
        Assert.Equal(token.Message.Key, index.Message.Key);
    }

    [Fact]
    public void An_index_this_repository_does_not_declare_stays_a_500()
    {
        // Only declared indexes are mapped; anything else is a defect rather than an expected outcome, and
        // is not dressed up as a business answer.
        Assert.Null(UniqueViolations.TryMap(UniqueViolationOn("ux_missions_something_nobody_declared")));
    }

    [Fact]
    public void The_mapping_recognises_the_violation_anywhere_in_the_exception_chain()
    {
        // Wolverine wraps what a handler's commit throws; the mapper walks the chain rather than trusting
        // the outermost type.
        var wrapped = new InvalidOperationException(
            "outer", UniqueViolationOn(MissionConfiguration.ActiveDriverUniqueIndex));

        Assert.Equal("CONCURRENCY_CONFLICT", UniqueViolations.TryMap(wrapped)!.Identity.Code);
    }
}
