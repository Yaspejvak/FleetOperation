using Fserp.FleetOperations.Modules.Fleet.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Fserp.FleetOperations.Modules.Fleet.Infrastructure.Persistence;

/// <summary>
/// Maps <see cref="Vehicle"/> to <c>fleet.vehicles</c>: statuses and type as strings, the two value
/// objects as single columns, a unique index on the plate number (F-2) and PostgreSQL's <c>xmin</c> as
/// the optimistic-concurrency token (decision 2). Applied to the host's one <c>AppDbContext</c>.
/// </summary>
public sealed class VehicleConfiguration : IEntityTypeConfiguration<Vehicle>
{
    /// <summary>The module's schema.</summary>
    public const string Schema = "fleet";

    /// <summary>The vehicles table.</summary>
    public const string Table = "vehicles";

    /// <summary>
    /// The unique index on the plate number. Its name is the contract between this mapping and the host's
    /// persistence exception mapper, which turns a violation into <c>409 VEHICLE_PLATE_NUMBER_ALREADY_REGISTERED</c>.
    /// </summary>
    public const string PlateNumberUniqueIndex = "ux_vehicles_plate_number";

    /// <summary>The shadow property carrying the PostgreSQL row version.</summary>
    public const string ConcurrencyToken = "xmin";

    // Enum names are short identifiers; the bound only keeps the column from being unbounded text.
    private const int EnumNameLength = 32;

    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Vehicle> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(Table, Schema);

        builder.HasKey(vehicle => vehicle.Id);
        builder.Property(vehicle => vehicle.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(vehicle => vehicle.PlateNumber)
            .HasColumnName("plate_number")
            .HasConversion(plate => plate.Value, value => PlateNumber.Create(value))
            .HasMaxLength(PlateNumber.MaxLength)
            .IsRequired();
        builder.HasIndex(vehicle => vehicle.PlateNumber)
            .IsUnique()
            .HasDatabaseName(PlateNumberUniqueIndex);

        builder.Property(vehicle => vehicle.VehicleType)
            .HasColumnName("vehicle_type")
            .HasConversion<string>()
            .HasMaxLength(EnumNameLength)
            .IsRequired();

        // numeric without precision keeps the decimal exactly as given: no unit or scale is invented here.
        builder.Property(vehicle => vehicle.Capacity)
            .HasColumnName("capacity_kg")
            .HasColumnType("numeric")
            .HasConversion(capacity => capacity.Kilograms, value => Capacity.FromKilograms(value))
            .IsRequired();

        builder.Property(vehicle => vehicle.OperationalStatus)
            .HasColumnName("operational_status")
            .HasConversion<string>()
            .HasMaxLength(EnumNameLength)
            .IsRequired();

        builder.Property(vehicle => vehicle.MaintenanceStatus)
            .HasColumnName("maintenance_status")
            .HasConversion<string>()
            .HasMaxLength(EnumNameLength)
            .IsRequired();

        builder.Property(vehicle => vehicle.CommittedMissionId)
            .HasColumnName("committed_mission_id");

        builder.Property(vehicle => vehicle.CreatedOnUtc)
            .HasColumnName("created_on_utc");
        builder.Property(vehicle => vehicle.ModifiedOnUtc)
            .HasColumnName("modified_on_utc");

        // PostgreSQL's system column: every UPDATE ... WHERE xmin = <read value> of a row someone else has
        // changed meanwhile matches zero rows, and EF Core raises a concurrency exception.
        builder.Property<uint>(ConcurrencyToken)
            .IsRowVersion();

        builder.Ignore(vehicle => vehicle.DisplayStatus);
        builder.Ignore(vehicle => vehicle.DomainEvents);
        builder.Ignore(vehicle => vehicle.IntegrationEvents);
    }
}
