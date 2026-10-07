using Fserp.FleetOperations.Modules.Drivers.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Fserp.FleetOperations.Modules.Drivers.Infrastructure.Persistence;

/// <summary>
/// Maps <see cref="Driver"/> to <c>drivers.drivers</c>: the status as a string, the name value object as a
/// single bounded column and PostgreSQL's <c>xmin</c> as the optimistic-concurrency token (decision 2).
/// Applied to the host's one <c>AppDbContext</c>.
/// </summary>
public sealed class DriverConfiguration : IEntityTypeConfiguration<Driver>
{
    /// <summary>The module's schema.</summary>
    public const string Schema = "drivers";

    /// <summary>The drivers table.</summary>
    public const string Table = "drivers";

    /// <summary>The shadow property carrying the PostgreSQL row version.</summary>
    public const string ConcurrencyToken = "xmin";

    // Enum names are short identifiers; the bound only keeps the column from being unbounded text.
    internal const int EnumNameLength = 32;

    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Driver> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(Table, Schema);

        builder.HasKey(driver => driver.Id);
        builder.Property(driver => driver.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        // L-17: the column carries the same 128-character bound the RegisterDriver validator measures on
        // the trimmed value, so the two can never disagree about what is storable.
        builder.Property(driver => driver.FullName)
            .HasColumnName("full_name")
            .HasConversion(name => name.Value, value => DriverName.Create(value))
            .HasMaxLength(DriverName.MaxLength)
            .IsRequired();

        builder.Property(driver => driver.OperationalStatus)
            .HasColumnName("operational_status")
            .HasConversion<string>()
            .HasMaxLength(EnumNameLength)
            .IsRequired();

        builder.Property(driver => driver.CommittedMissionId)
            .HasColumnName("committed_mission_id");

        builder.Property(driver => driver.CreatedOnUtc)
            .HasColumnName("created_on_utc");
        builder.Property(driver => driver.ModifiedOnUtc)
            .HasColumnName("modified_on_utc");

        // PostgreSQL's system column: every UPDATE ... WHERE xmin = <read value> of a row someone else has
        // changed meanwhile matches zero rows, and EF Core raises a concurrency exception. It covers the
        // driver row, which is what a commitment and a status change both write.
        builder.Property<uint>(ConcurrencyToken)
            .IsRowVersion();

        // The qualifications are the driver's own children: loaded with it, deleted with it, and reached
        // only through the aggregate. The navigation is read through the backing field, because the
        // aggregate exposes the list read-only.
        builder.HasMany(driver => driver.Qualifications)
            .WithOne()
            .HasForeignKey(QualificationConfiguration.DriverIdColumn)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Metadata
            .FindNavigation(nameof(Driver.Qualifications))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);

        builder.Ignore(driver => driver.DomainEvents);
        builder.Ignore(driver => driver.IntegrationEvents);
    }
}
