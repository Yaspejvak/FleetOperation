using System.Globalization;
using System.Resources;
using Fserp.FleetOperations.Api.Hosting;
using Fserp.FleetOperations.Api.Resources;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using MPCore.Application.Results;
using Npgsql;

namespace Fserp.FleetOperations.UnitTests.Host;

public sealed class ConcurrencyExceptionMapperTests
{
    [Fact]
    public void A_lost_concurrency_race_at_commit_is_the_host_wide_409_failure()
    {
        // No entries: the mapping must not depend on which entity lost the race.
        var thrown = new DbUpdateConcurrencyException("The row was changed by another request.");

        var failure = new ConcurrencyExceptionMapper().Map(thrown, new DefaultHttpContext());

        Assert.NotNull(failure);
        Assert.Equal(new ErrorIdentity("fleetoperations", "CONCURRENCY_CONFLICT"), failure.Identity);
        Assert.Equal(ErrorCategory.Concurrency, failure.Category);
        Assert.Equal("fleetoperations.concurrency_conflict", failure.Message.Key);
    }

    [Fact]
    public void The_race_is_found_however_deeply_it_is_wrapped()
    {
        var thrown = new InvalidOperationException(
            "outer",
            new AggregateException(new DbUpdateConcurrencyException("lost")));

        // AggregateException exposes its first inner exception as InnerException.
        Assert.NotNull(ConcurrencyExceptionMapper.TryMap(thrown));
    }

    [Fact]
    public void A_plain_save_failure_stays_unmapped()
    {
        // DbUpdateConcurrencyException derives from DbUpdateException, not the other way round.
        Assert.Null(ConcurrencyExceptionMapper.TryMap(new DbUpdateException("save failed")));
    }

    [Fact]
    public void A_unique_violation_is_left_to_its_own_mapper()
    {
        var thrown = new DbUpdateException(
            "save failed",
            new PostgresException("duplicate key", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation, constraintName: "ux_vehicles_plate_number"));

        Assert.Null(ConcurrencyExceptionMapper.TryMap(thrown));
    }

    [Fact]
    public void An_unrelated_exception_stays_unmapped()
    {
        Assert.Null(ConcurrencyExceptionMapper.TryMap(new InvalidOperationException("boom")));
        Assert.Null(ConcurrencyExceptionMapper.TryMap(null));
    }
}

public sealed class HostMessagesTests
{
    [Fact]
    public void The_concurrency_conflict_key_has_an_English_text()
    {
        // The catalog reads the resource named after the marker type, as MP Core's AddResources<T> does.
        var resources = new ResourceManager(typeof(HostMessages));
        var key = HostFailures.MessageKey(HostFailures.ConcurrencyConflict);

        Assert.Equal("fleetoperations.concurrency_conflict", key);
        Assert.False(string.IsNullOrWhiteSpace(resources.GetString(key, CultureInfo.InvariantCulture)), $"No text for {key}.");
    }
}
