using Fserp.FleetOperations.Modules.Drivers.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Fserp.FleetOperations.Modules.Drivers.Infrastructure.Persistence;

/// <summary>
/// Maps <see cref="Qualification"/> to <c>drivers.qualifications</c>: the vehicle type as a string, the
/// owning driver as a shadow foreign key, and a unique index on the pair (lead decision L-18).
/// </summary>
public sealed class QualificationConfiguration : IEntityTypeConfiguration<Qualification>
{
    /// <summary>The qualifications table, in the Drivers schema.</summary>
    public const string Table = "qualifications";

    /// <summary>
    /// The shadow foreign key to the owning driver. A qualification has no navigation back to its driver:
    /// it is reached only through the aggregate.
    /// </summary>
    public const string DriverIdColumn = "driver_id";

    /// <summary>
    /// The unique index behind <c>DRIVER_QUALIFICATION_DUPLICATE</c>. A driver cannot hold the same vehicle
    /// type twice even if a future code path forgets the rule, exactly as the plate's unique index
    /// backstops F-2.
    /// </summary>
    public const string VehicleTypeUniqueIndex = "ux_qualifications_driver_id_vehicle_type";

    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Qualification> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(Table, DriverConfiguration.Schema);

        builder.HasKey(qualification => qualification.Id);
        builder.Property(qualification => qualification.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property<Guid>(DriverIdColumn);

        builder.Property(qualification => qualification.VehicleType)
            .HasColumnName("vehicle_type")
            .HasConversion<string>()
            .HasMaxLength(DriverConfiguration.EnumNameLength)
            .IsRequired();

        builder.Property(qualification => qualification.CreatedOnUtc)
            .HasColumnName("created_on_utc");
        builder.Property(qualification => qualification.ModifiedOnUtc)
            .HasColumnName("modified_on_utc");

        builder.HasIndex(DriverIdColumn, nameof(Qualification.VehicleType))
            .IsUnique()
            .HasDatabaseName(VehicleTypeUniqueIndex);
    }
}
