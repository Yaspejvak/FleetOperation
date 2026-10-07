using System.Globalization;
using Fserp.FleetOperations.Modules.Operations.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Fserp.FleetOperations.Modules.Operations.Infrastructure.Persistence;

/// <summary>
/// Maps <see cref="Mission"/> to <c>operations.missions</c>: the status as a string so the partial-index
/// predicate reads plainly, the two value objects as single columns, PostgreSQL's <c>xmin</c> as the
/// optimistic-concurrency token, and the two unique partial indexes of decision 2. Applied to the host's
/// one <c>AppDbContext</c>.
/// </summary>
public sealed class MissionConfiguration : IEntityTypeConfiguration<Mission>
{
    /// <summary>The module's schema.</summary>
    public const string Schema = "operations";

    /// <summary>The missions table.</summary>
    public const string Table = "missions";

    /// <summary>
    /// A vehicle holds at most one mission in <c>Assigned</c> or <c>InProgress</c> (O-8). Its name is the
    /// contract between this mapping and the host's persistence exception mapper, which turns a violation
    /// into a <c>409</c> rather than a <c>500</c>.
    /// </summary>
    public const string ActiveVehicleUniqueIndex = "ux_missions_active_vehicle";

    /// <summary>The same for a driver.</summary>
    public const string ActiveDriverUniqueIndex = "ux_missions_active_driver";

    /// <summary>The shadow property carrying the PostgreSQL row version.</summary>
    public const string ConcurrencyToken = "xmin";

    /// <summary>The status column, named here because the index filters are raw SQL over it.</summary>
    public const string StatusColumn = "status";

    // Enum names are short identifiers; the bound only keeps the column from being unbounded text.
    private const int EnumNameLength = 32;

    /// <summary>
    /// The predicate of both partial indexes: the statuses in which a mission holds its resources
    /// (<see cref="MissionActivity.ResourceHoldingStatuses"/>), written from that one list so the index and
    /// the Domain's definition of "conflicting" cannot drift apart.
    /// </summary>
    public static string ResourceHoldingFilter { get; } = string.Create(
        CultureInfo.InvariantCulture,
        $"{StatusColumn} IN ({string.Join(", ", MissionActivity.ResourceHoldingStatuses.Select(status => $"'{status}'"))})");

    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Mission> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(Table, Schema);

        builder.HasKey(mission => mission.Id);
        builder.Property(mission => mission.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        // text, not a bounded string: O-7 names one rule for a location — non-empty — and a length the
        // plan does not name would be an invented limit.
        builder.Property(mission => mission.Origin)
            .HasColumnName("origin")
            .HasColumnType("text")
            .HasConversion(location => location.Value, value => Location.Create(value))
            .IsRequired();

        builder.Property(mission => mission.Destination)
            .HasColumnName("destination")
            .HasColumnType("text")
            .HasConversion(location => location.Value, value => Location.Create(value))
            .IsRequired();

        // numeric without precision keeps the decimal exactly as given: no unit or scale is invented here.
        builder.Property(mission => mission.RequiredCapacity)
            .HasColumnName("required_capacity_kg")
            .HasColumnType("numeric")
            .HasConversion(capacity => capacity.Kilograms, value => RequiredCapacity.FromKilograms(value))
            .IsRequired();

        builder.Property(mission => mission.ScheduledAt)
            .HasColumnName("scheduled_at");

        builder.Property(mission => mission.AssignedVehicleId)
            .HasColumnName("assigned_vehicle_id");

        builder.Property(mission => mission.AssignedDriverId)
            .HasColumnName("assigned_driver_id");

        // Stored as a string, by the plan: the two index predicates below then read as the state machine
        // writes them, and a trail or a dump says 'InProgress' rather than 3.
        builder.Property(mission => mission.Status)
            .HasColumnName(StatusColumn)
            .HasConversion<string>()
            .HasMaxLength(EnumNameLength)
            .IsRequired();

        builder.Property(mission => mission.CreatedOnUtc)
            .HasColumnName("created_on_utc");
        builder.Property(mission => mission.ModifiedOnUtc)
            .HasColumnName("modified_on_utc");

        // Decision 2, the set-based half of the guarantee: even a code path that never touched the vehicle
        // row cannot leave two active missions holding one vehicle, because PostgreSQL refuses the second
        // row. The filter makes the uniqueness apply to active missions only, so a completed or cancelled
        // mission keeps its ids without blocking the next assignment.
        builder.HasIndex(mission => mission.AssignedVehicleId)
            .IsUnique()
            .HasFilter(ResourceHoldingFilter)
            .HasDatabaseName(ActiveVehicleUniqueIndex);

        builder.HasIndex(mission => mission.AssignedDriverId)
            .IsUnique()
            .HasFilter(ResourceHoldingFilter)
            .HasDatabaseName(ActiveDriverUniqueIndex);

        // PostgreSQL's system column: every UPDATE ... WHERE xmin = <read value> of a row someone else has
        // changed meanwhile matches zero rows, and EF Core raises a concurrency exception. Two operators
        // acting on one mission is exactly the case the plan names.
        builder.Property<uint>(ConcurrencyToken)
            .IsRowVersion();

        builder.Ignore(mission => mission.DomainEvents);
        builder.Ignore(mission => mission.IntegrationEvents);
    }
}
