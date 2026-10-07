using Fserp.FleetOperations.Api.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using MPCore.Application.Results;
using Npgsql;

namespace Fserp.FleetOperations.UnitTests.Host;

public sealed class UniqueViolationExceptionMapperTests
{
    private static PostgresException Violation(string sqlState, string? constraint) =>
        new("duplicate key value violates unique constraint", "ERROR", "ERROR", sqlState, constraintName: constraint);

    [Fact]
    public void A_plate_number_index_violation_at_commit_is_the_409_of_the_plate_rule()
    {
        var thrown = new DbUpdateException("save failed", Violation(PostgresErrorCodes.UniqueViolation, "ux_vehicles_plate_number"));

        var failure = new UniqueViolationExceptionMapper().Map(thrown, new DefaultHttpContext());

        Assert.NotNull(failure);
        Assert.Equal(new ErrorIdentity("fleet", "VEHICLE_PLATE_NUMBER_ALREADY_REGISTERED"), failure.Identity);
        Assert.Equal(ErrorCategory.AlreadyExists, failure.Category);
        Assert.Equal("fleet.vehicle_plate_number_already_registered", failure.Message.Key);
    }

    [Fact]
    public void The_violation_is_found_however_deeply_it_is_wrapped()
    {
        var thrown = new InvalidOperationException(
            "outer",
            new DbUpdateException("save failed", Violation(PostgresErrorCodes.UniqueViolation, "ux_vehicles_plate_number")));

        Assert.NotNull(UniqueViolations.TryMap(thrown));
    }

    [Fact]
    public void A_violation_of_an_unknown_index_stays_unmapped()
    {
        var thrown = new DbUpdateException("save failed", Violation(PostgresErrorCodes.UniqueViolation, "ux_something_else"));

        Assert.Null(UniqueViolations.TryMap(thrown));
    }

    [Fact]
    public void Another_PostgreSQL_error_on_the_same_index_stays_unmapped()
    {
        var thrown = new DbUpdateException("save failed", Violation(PostgresErrorCodes.ForeignKeyViolation, "ux_vehicles_plate_number"));

        Assert.Null(UniqueViolations.TryMap(thrown));
    }

    [Fact]
    public void An_unrelated_exception_stays_unmapped()
    {
        Assert.Null(UniqueViolations.TryMap(new InvalidOperationException("boom")));
        Assert.Null(UniqueViolations.TryMap(null));
    }
}
