using Fserp.FleetOperations.Infrastructure.Persistence;
using Fserp.FleetOperations.Modules.Fleet.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using MPCore.Domain.Events;
using MPCore.Persistence.EntityFrameworkCore.PostgreSql;

namespace Fserp.FleetOperations.UnitTests.Fleet;

/// <summary>
/// The EF Core model of <see cref="Vehicle"/> as the host's context builds it. No database is contacted:
/// the model and the migration snapshot are compared in memory. Behaviour against PostgreSQL (the unique
/// index refusing a duplicate, xmin refusing a lost update) is in the integration tests.
/// </summary>
public sealed class VehicleModelTests : IDisposable
{
    private readonly AppDbContext _context;
    private readonly IEntityType _vehicle;

    public VehicleModelTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        PostgreSqlDbContextOptions.Apply(options, "Host=model-only.invalid;Database=never_contacted");
        _context = new AppDbContext(options.Options, TimeProvider.System, NullAggregateEventSink.Instance);
        _vehicle = _context.Model.FindEntityType(typeof(Vehicle))!;
    }

    public void Dispose() => _context.Dispose();

    [Fact]
    public void Lives_in_fleet_vehicles()
    {
        Assert.Equal("fleet", _vehicle.GetSchema());
        Assert.Equal("vehicles", _vehicle.GetTableName());
    }

    [Fact]
    public void The_plate_number_has_a_unique_index_with_the_name_the_exception_mapper_knows()
    {
        var index = Assert.Single(_vehicle.GetIndexes(), candidate => candidate.Properties.Single().Name == nameof(Vehicle.PlateNumber));

        Assert.True(index.IsUnique);
        Assert.Equal("ux_vehicles_plate_number", index.GetDatabaseName());
    }

    [Theory]
    [InlineData(nameof(Vehicle.VehicleType), "vehicle_type")]
    [InlineData(nameof(Vehicle.OperationalStatus), "operational_status")]
    [InlineData(nameof(Vehicle.MaintenanceStatus), "maintenance_status")]
    [InlineData(nameof(Vehicle.PlateNumber), "plate_number")]
    public void Statuses_type_and_plate_are_stored_as_strings(string property, string column)
    {
        var mapped = _vehicle.FindProperty(property)!;

        // The type mapping carries the converter actually used at run time, whether it was given as an
        // instance (the value objects) or as a provider type (HasConversion<string>() on the enums).
        Assert.Equal(typeof(string), mapped.GetTypeMapping().Converter!.ProviderClrType);
        Assert.Equal(column, mapped.GetColumnName());
    }

    [Fact]
    public void The_plate_number_column_holds_at_most_16_characters()
    {
        // Owner decision (round 1 closure); the same bound as PlateNumber.MaxLength.
        var mapped = _vehicle.FindProperty(nameof(Vehicle.PlateNumber))!;

        Assert.Equal(16, mapped.GetMaxLength());
        Assert.Equal("character varying(16)", mapped.GetColumnType());
    }

    [Fact]
    public void A_migration_bounds_the_plate_number()
    {
        Assert.Contains(_context.Database.GetMigrations(), migration => migration.EndsWith("_LimitVehiclePlateNumberLength", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(nameof(Vehicle.VehicleType))]
    [InlineData(nameof(Vehicle.OperationalStatus))]
    [InlineData(nameof(Vehicle.MaintenanceStatus))]
    public void Enums_are_stored_by_member_name(string property)
    {
        var converter = _vehicle.FindProperty(property)!.GetTypeMapping().Converter!;

        var stored = converter.ConvertToProvider(Enum.ToObject(converter.ModelClrType, 1));

        Assert.Equal(Enum.GetName(converter.ModelClrType, 1), stored);
    }

    [Fact]
    public void Capacity_is_stored_as_an_exact_numeric_in_kilograms()
    {
        var mapped = _vehicle.FindProperty(nameof(Vehicle.Capacity))!;

        Assert.Equal(typeof(decimal), mapped.GetTypeMapping().Converter!.ProviderClrType);
        Assert.Equal("numeric", mapped.GetColumnType());
        Assert.Equal("capacity_kg", mapped.GetColumnName());
    }

    [Fact]
    public void The_concurrency_token_is_PostgreSQL_xmin()
    {
        var token = Assert.Single(_vehicle.GetProperties(), property => property.IsConcurrencyToken);

        Assert.Equal("xmin", token.GetColumnName());
        Assert.Equal("xid", token.GetColumnType());
        Assert.True(token.IsShadowProperty());
        Assert.Equal(ValueGenerated.OnAddOrUpdate, token.ValueGenerated);
    }

    [Fact]
    public void Derived_and_event_members_are_not_mapped()
    {
        Assert.Null(_vehicle.FindProperty(nameof(Vehicle.DisplayStatus)));
        Assert.Null(_vehicle.FindNavigation(nameof(Vehicle.DomainEvents)));
        Assert.Null(_vehicle.FindProperty(nameof(Vehicle.DomainEvents)));
    }

    [Fact]
    public void The_migrations_match_the_model()
    {
        // A mapping change without a migration fails here.
        Assert.False(_context.Database.HasPendingModelChanges());
    }

    [Fact]
    public void A_migration_exists()
    {
        Assert.Contains(_context.Database.GetMigrations(), migration => migration.EndsWith("_InitialFleetVehicles", StringComparison.Ordinal));
    }
}
