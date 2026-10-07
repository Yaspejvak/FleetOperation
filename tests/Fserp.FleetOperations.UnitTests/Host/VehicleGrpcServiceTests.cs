using Fserp.FleetOperations.Api.Hosting;
using Fserp.FleetOperations.Modules.Fleet.Application;
using Fserp.FleetOperations.Modules.Fleet.Application.Queries;
using Fserp.FleetOperations.Modules.Fleet.Application.Views;
using Fserp.FleetOperations.Modules.Fleet.Contracts;
using Fserp.FleetOperations.Modules.Fleet.Domain;
using Fserp.FleetOperations.Modules.Fleet.Domain.Rules;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MPCore.Application.Results;
using MPCore.Domain.Rules;
using MPCore.Transport.Grpc;
using Npgsql;
using static Fserp.FleetOperations.UnitTests.Host.FleetOperationsHostFactory;
using FleetDomain = Fserp.FleetOperations.Modules.Fleet.Domain;
using FleetContracts = Fserp.FleetOperations.Modules.Fleet.Contracts;
using Proto = Fserp.FleetOperations.Api.Grpc.Fleet.V1;

namespace Fserp.FleetOperations.UnitTests.Host;

/// <summary>
/// The gRPC Fleet read surface through the real host pipeline (docs/architecture.md, "Transport parity").
/// A real gRPC client speaks to the in-memory server, so every status below is one a caller observes
/// rather than one read off the mapping code.
/// </summary>
public sealed class VehicleGrpcServiceTests : IClassFixture<FleetOperationsHostFactory>, IDisposable
{
    private readonly FleetOperationsHostFactory _host;
    private readonly List<GrpcChannel> _channels = [];

    public VehicleGrpcServiceTests(FleetOperationsHostFactory host)
    {
        _host = host;
        _host.Bus.Reset(static _ => throw new InvalidOperationException("Each test sets its response."));
    }

    public void Dispose()
    {
        foreach (var channel in _channels)
        {
            channel.Dispose();
        }
    }

    private Proto.VehicleService.VehicleServiceClient Client()
    {
        var handler = _host.Server.CreateHandler();
        var channel = GrpcChannel.ForAddress(
            _host.Server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = handler, Credentials = ChannelCredentials.Insecure });
        _channels.Add(channel);
        var client = new Proto.VehicleService.VehicleServiceClient(channel);
        return client;
    }

    private static Metadata Bearer(string token) => new() { { "Authorization", "Bearer " + token } };

    private static Exception Thrown(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected the action to throw.");
    }

    private static VehicleView View(Guid id, Guid? mission = null) =>
        new(
            id,
            "AB-1",
            FleetContracts.VehicleType.HeavyTruck,
            1250.5m,
            FleetDomain.OperationalStatus.Active,
            FleetDomain.MaintenanceStatus.NotUnderMaintenance,
            VehicleDisplayStatus.Active,
            mission);

    [Fact]
    public async Task GetVehicle_sends_the_same_query_record_REST_sends_and_maps_the_view()
    {
        var id = Guid.CreateVersion7();
        _host.Bus.Reset(_ => Result<VehicleView>.Success(View(id)));

        var response = await Client().GetVehicleAsync(
            new Proto.GetVehicleRequest { VehicleId = id.ToString() },
            Bearer(_host.Token(OperatorRole)));

        // The very same Application query the REST endpoint builds: one use case, two adapters.
        Assert.Equal(new GetVehicle(id), Assert.Single(_host.Bus.Sent));
        Assert.Equal(id.ToString(), response.Vehicle.VehicleId);
        Assert.Equal("AB-1", response.Vehicle.PlateNumber);
        // Proto enums travel as numbers by design; F-9's REST-only converter does not apply.
        Assert.Equal(Proto.VehicleType.HeavyTruck, response.Vehicle.VehicleType);
        Assert.Equal(Proto.OperationalStatus.Active, response.Vehicle.OperationalStatus);
        Assert.Equal(Proto.MaintenanceStatus.NotUnderMaintenance, response.Vehicle.MaintenanceStatus);
        Assert.Equal(Proto.VehicleStatus.Active, response.Vehicle.Status);
        // Decimal in invariant culture, because protobuf has no decimal and a double would change it.
        Assert.Equal("1250.5", response.Vehicle.CapacityKg);
        Assert.False(response.Vehicle.HasCommittedMissionId);
    }

    [Fact]
    public async Task A_committed_vehicle_carries_the_optional_mission_id()
    {
        var id = Guid.CreateVersion7();
        var mission = Guid.CreateVersion7();
        _host.Bus.Reset(_ => Result<VehicleView>.Success(View(id, mission)));

        var response = await Client().GetVehicleAsync(
            new Proto.GetVehicleRequest { VehicleId = id.ToString() },
            Bearer(_host.Token(OperatorRole)));

        Assert.True(response.Vehicle.HasCommittedMissionId);
        Assert.Equal(mission.ToString(), response.Vehicle.CommittedMissionId);
    }

    [Fact]
    public async Task An_unknown_vehicle_is_NotFound_under_the_same_failure_REST_renders()
    {
        // The parity that matters: one FailureDescriptor, two transports.
        _host.Bus.Reset(static _ => Result<VehicleView>.FromFailure(FleetFailures.VehicleNotFound()));

        var refused = await Assert.ThrowsAsync<RpcException>(() => Client().GetVehicleAsync(
            new Proto.GetVehicleRequest { VehicleId = Guid.CreateVersion7().ToString() },
            Bearer(_host.Token(OperatorRole))).ResponseAsync);

        Assert.Equal(StatusCode.NotFound, refused.StatusCode);
        Assert.Equal(ErrorCategory.NotFound, FleetFailures.VehicleNotFound().Category);
        Assert.Equal("VEHICLE_NOT_FOUND", FleetFailures.VehicleNotFound().Identity.Code);
    }

    [Fact]
    public async Task A_vehicle_id_that_is_not_a_uuid_is_a_validation_failure_and_not_a_fabricated_code()
    {
        // L-7: Guid.TryParse yields Guid.Empty, which GetVehicleValidator's NotEmpty() refuses. The host
        // bus is a stand-in here, so the record reaching it is what the assertion pins; the status is the
        // one MP Core renders for the validation failure the real validator produces.
        _host.Bus.Reset(static _ => Result<VehicleView>.FromFailure(
            new FailureDescriptor(
                new ErrorIdentity("mpcore.validation", "VALIDATION_FAILED"),
                ErrorCategory.Validation,
                new FailureMessageDescriptor("mpcore.validation.failed", null),
                null,
                null)));

        var refused = await Assert.ThrowsAsync<RpcException>(() => Client().GetVehicleAsync(
            new Proto.GetVehicleRequest { VehicleId = "not-a-uuid" },
            Bearer(_host.Token(OperatorRole))).ResponseAsync);

        Assert.Equal(StatusCode.InvalidArgument, refused.StatusCode);
        Assert.Equal(new GetVehicle(Guid.Empty), Assert.Single(_host.Bus.Sent));
    }

    [Fact]
    public async Task A_broken_rule_is_FailedPrecondition_under_the_rules_own_code()
    {
        _host.Bus.Reset(_ => throw Thrown(() =>
            BusinessRules.Check(new VehicleAlreadyUnderMaintenanceRule(FleetDomain.MaintenanceStatus.UnderMaintenance))));

        var refused = await Assert.ThrowsAsync<RpcException>(() => Client().GetVehicleAsync(
            new Proto.GetVehicleRequest { VehicleId = Guid.CreateVersion7().ToString() },
            Bearer(_host.Token(OperatorRole))).ResponseAsync);

        Assert.Equal(StatusCode.FailedPrecondition, refused.StatusCode);
    }

    [Fact]
    public async Task GetAvailableVehicles_sends_the_same_query_record_REST_sends_and_maps_every_row()
    {
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        _host.Bus.Reset(_ => (IReadOnlyList<AvailableVehicleView>)
        [
            new AvailableVehicleView(first, "AB-1", FleetContracts.VehicleType.Van, 950.25m),
            new AvailableVehicleView(second, "AB-2", FleetContracts.VehicleType.Truck, 3000m),
        ]);

        var response = await Client().GetAvailableVehiclesAsync(
            new Proto.GetAvailableVehiclesRequest(),
            Bearer(_host.Token(FleetManagerRole)));

        Assert.IsType<GetAvailableVehicles>(Assert.Single(_host.Bus.Sent));
        Assert.Equal(2, response.Vehicles.Count);
        Assert.Equal(first.ToString(), response.Vehicles[0].VehicleId);
        Assert.Equal(Proto.VehicleType.Van, response.Vehicles[0].VehicleType);
        Assert.Equal("950.25", response.Vehicles[0].CapacityKg);
        Assert.Equal(Proto.VehicleType.Truck, response.Vehicles[1].VehicleType);
        Assert.Equal("3000", response.Vehicles[1].CapacityKg);
    }

    [Fact]
    public async Task An_empty_fleet_is_an_empty_repeated_field()
    {
        _host.Bus.Reset(static _ => (IReadOnlyList<AvailableVehicleView>)[]);

        var response = await Client().GetAvailableVehiclesAsync(
            new Proto.GetAvailableVehiclesRequest(),
            Bearer(_host.Token(OperatorRole)));

        Assert.Empty(response.Vehicles);
    }

    [Fact]
    public async Task No_token_is_Unauthenticated_and_never_reaches_the_bus()
    {
        _host.Bus.Reset(static _ => Result<VehicleView>.Success(View(Guid.CreateVersion7())));

        var refused = await Assert.ThrowsAsync<RpcException>(() => Client().GetVehicleAsync(
            new Proto.GetVehicleRequest { VehicleId = Guid.CreateVersion7().ToString() }).ResponseAsync);

        Assert.Equal(StatusCode.Unauthenticated, refused.StatusCode);
        Assert.Empty(_host.Bus.Sent);
    }

    [Theory]
    [InlineData(AdministratorRole)]
    [InlineData(null)]
    public async Task A_caller_without_a_reader_role_is_PermissionDenied_and_never_reaches_the_bus(string? role)
    {
        // Decision 5: the gRPC reads carry the same OperationalReader policy as the REST reads.
        // AD-1: the administrator is not a superset role.
        _host.Bus.Reset(static _ => Result<VehicleView>.Success(View(Guid.CreateVersion7())));
        var token = role is null ? _host.Token() : _host.Token(role);

        var refused = await Assert.ThrowsAsync<RpcException>(() => Client().GetVehicleAsync(
            new Proto.GetVehicleRequest { VehicleId = Guid.CreateVersion7().ToString() },
            Bearer(token)).ResponseAsync);

        Assert.Equal(StatusCode.PermissionDenied, refused.StatusCode);
        Assert.Empty(_host.Bus.Sent);
    }

    [Fact]
    public async Task The_available_list_over_gRPC_carries_the_same_policy()
    {
        _host.Bus.Reset(static _ => (IReadOnlyList<AvailableVehicleView>)[]);

        var refused = await Assert.ThrowsAsync<RpcException>(() => Client().GetAvailableVehiclesAsync(
            new Proto.GetAvailableVehiclesRequest(),
            Bearer(_host.Token(AdministratorRole))).ResponseAsync);

        Assert.Equal(StatusCode.PermissionDenied, refused.StatusCode);
        Assert.Empty(_host.Bus.Sent);
    }

    [Fact]
    public async Task A_unique_violation_answers_over_gRPC_too_which_proves_the_mapper_is_registered()
    {
        // F-10: the gRPC twin of UniqueViolationExceptionMapper, resolved by MP Core's interceptor from
        // the container. Without the registration this would be the generic internal failure.
        _host.Bus.Reset(static _ => throw new DbUpdateException(
            "save failed",
            new PostgresException(
                "duplicate key value violates unique constraint",
                "ERROR",
                "ERROR",
                PostgresErrorCodes.UniqueViolation,
                constraintName: "ux_vehicles_plate_number")));

        var refused = await Assert.ThrowsAsync<RpcException>(() => Client().GetVehicleAsync(
            new Proto.GetVehicleRequest { VehicleId = Guid.CreateVersion7().ToString() },
            Bearer(_host.Token(OperatorRole))).ResponseAsync);

        Assert.Equal(StatusCode.AlreadyExists, refused.StatusCode);
    }

    [Fact]
    public async Task A_lost_concurrency_race_answers_over_gRPC_too()
    {
        // F-10: the gRPC twin of ConcurrencyExceptionMapper.
        _host.Bus.Reset(static _ => throw new DbUpdateConcurrencyException("The row was changed."));

        var refused = await Assert.ThrowsAsync<RpcException>(() => Client().GetVehicleAsync(
            new Proto.GetVehicleRequest { VehicleId = Guid.CreateVersion7().ToString() },
            Bearer(_host.Token(OperatorRole))).ResponseAsync);

        Assert.Equal(StatusCode.Aborted, refused.StatusCode);
    }

    [Fact]
    public async Task An_unmapped_unique_violation_stays_an_internal_failure_on_gRPC_as_it_does_on_REST()
    {
        _host.Bus.Reset(static _ => throw new DbUpdateException(
            "save failed",
            new PostgresException(
                "duplicate key value violates unique constraint",
                "ERROR",
                "ERROR",
                PostgresErrorCodes.UniqueViolation,
                constraintName: "ux_something_else")));

        var refused = await Assert.ThrowsAsync<RpcException>(() => Client().GetVehicleAsync(
            new Proto.GetVehicleRequest { VehicleId = Guid.CreateVersion7().ToString() },
            Bearer(_host.Token(OperatorRole))).ResponseAsync);

        Assert.Equal(StatusCode.Internal, refused.StatusCode);
    }
}

/// <summary>
/// F-10 at the mapper level: each gRPC mapper answers the identical <see cref="FailureDescriptor"/> its
/// HTTP twin answers, because both call the same static entry point. No mapping table is copied.
/// </summary>
public sealed class GrpcExceptionMapperParityTests
{
    private static readonly UniqueViolationGrpcExceptionMapper UniqueGrpc = new();
    private static readonly ConcurrencyGrpcExceptionMapper ConcurrencyGrpc = new();

    private static DbUpdateException UniqueViolationOn(string index) =>
        new(
            "save failed",
            new PostgresException(
                "duplicate key value violates unique constraint",
                "ERROR",
                "ERROR",
                PostgresErrorCodes.UniqueViolation,
                constraintName: index));

    private static void AssertSameDescriptor(FailureDescriptor? http, FailureDescriptor? grpc)
    {
        Assert.NotNull(http);
        Assert.NotNull(grpc);
        Assert.Equal(http.Identity, grpc.Identity);
        Assert.Equal(http.Category, grpc.Category);
        Assert.Equal(http.Message.Key, grpc.Message.Key);
    }

    [Fact]
    public void The_unique_violation_mappers_answer_the_same_descriptor()
    {
        var exception = UniqueViolationOn("ux_vehicles_plate_number");

        AssertSameDescriptor(
            UniqueViolations.TryMap(exception),
            UniqueGrpc.Map(exception, null!));
    }

    [Fact]
    public void The_unique_violation_mappers_agree_on_an_undeclared_index()
    {
        var exception = UniqueViolationOn("ux_something_else");

        Assert.Null(UniqueViolations.TryMap(exception));
        Assert.Null(UniqueGrpc.Map(exception, null!));
    }

    [Fact]
    public void The_concurrency_mappers_answer_the_same_descriptor()
    {
        var exception = new DbUpdateConcurrencyException("The row was changed.");

        AssertSameDescriptor(
            ConcurrencyExceptionMapper.TryMap(exception),
            ConcurrencyGrpc.Map(exception, null!));
    }

    [Fact]
    public void The_concurrency_mappers_agree_that_an_unrelated_exception_is_not_theirs()
    {
        var exception = new InvalidOperationException("something else");

        Assert.Null(ConcurrencyExceptionMapper.TryMap(exception));
        Assert.Null(ConcurrencyGrpc.Map(exception, null!));
    }

    [Fact]
    public void Each_gRPC_mapper_recognises_its_exception_anywhere_in_the_chain()
    {
        var wrapped = new InvalidOperationException("outer", UniqueViolationOn("ux_vehicles_plate_number"));
        var wrappedConcurrency = new InvalidOperationException("outer", new DbUpdateConcurrencyException("inner"));

        Assert.Equal("VEHICLE_PLATE_NUMBER_ALREADY_REGISTERED", UniqueGrpc.Map(wrapped, null!)!.Identity.Code);
        Assert.Equal("CONCURRENCY_CONFLICT", ConcurrencyGrpc.Map(wrappedConcurrency, null!)!.Identity.Code);
    }

    [Fact]
    public void Both_gRPC_mappers_are_registered_in_the_host()
    {
        using var host = new FleetOperationsHostFactory();
        using var scope = host.Services.CreateScope();

        var mappers = scope.ServiceProvider
            .GetServices<IGrpcExceptionMapper>()
            .Select(mapper => mapper.GetType().Name)
            .ToList();

        Assert.Contains(nameof(UniqueViolationGrpcExceptionMapper), mappers);
        Assert.Contains(nameof(ConcurrencyGrpcExceptionMapper), mappers);
    }
}
