using Fserp.FleetOperations.Modules.Fleet.Application.Ports;
using Fserp.FleetOperations.Modules.Fleet.Application.Views;
using Fserp.FleetOperations.Modules.Fleet.Contracts;
using Fserp.FleetOperations.Modules.Fleet.Domain;
using MPCore.Application.Messaging;
using MPCore.Application.Results;
using MPCore.Application.Time;
using MPCore.Audit;
using MPCore.Persistence.Abstractions;

namespace Fserp.FleetOperations.Modules.Fleet.Application.Commands;

/// <summary>Registers a vehicle. It starts Active and not under maintenance (F-5).</summary>
/// <param name="PlateNumber">The plate number as entered; normalized by trimming and upper-casing.</param>
/// <param name="VehicleType">The vehicle type.</param>
/// <param name="CapacityKg">The capacity in kilograms; positive.</param>
public sealed record RegisterVehicle(string PlateNumber, VehicleType VehicleType, decimal CapacityKg)
    : ICommand<Result<VehicleView>>;

/// <summary>Handles <see cref="RegisterVehicle"/>. One transaction, owned by the middleware.</summary>
public static class RegisterVehicleHandler
{
    /// <summary>Registers the vehicle, or answers <c>409</c> when the plate number is already registered.</summary>
    /// <param name="command">The command.</param>
    /// <param name="vehicles">The vehicle repository.</param>
    /// <param name="unitOfWork">The unit of work the middleware commits; declared so the handler runs in its transaction.</param>
    /// <param name="clock">The clock the identity's timestamp comes from.</param>
    /// <param name="audit">The business audit recorder.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public static async Task<Result<VehicleView>> Handle(
        RegisterVehicle command,
        IVehicleRepository vehicles,
        IUnitOfWork unitOfWork,
        IClock clock,
        IBusinessAuditRecorder audit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(vehicles);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(audit);

        // Validate first: the value objects check their own rules, then the plate's uniqueness is
        // checked before anything is tracked, so the failure is returned with nothing pending.
        var plateNumber = PlateNumber.Create(command.PlateNumber);
        var capacity = Capacity.FromKilograms(command.CapacityKg);
        if (await vehicles.PlateNumberExistsAsync(plateNumber, cancellationToken).ConfigureAwait(false))
        {
            return Result<VehicleView>.FromFailure(FleetFailures.PlateNumberAlreadyRegistered());
        }

        // Mutate second. A concurrent registration of the same plate that passed the check above is
        // refused by the unique index at commit and reaches the caller as the same 409.
        var vehicle = Vehicle.Register(Guid.CreateVersion7(clock.UtcNow), plateNumber, command.VehicleType, capacity);
        vehicles.Add(vehicle);
        await audit.RecordAsync(
                FleetAudit.Module,
                FleetAudit.VehicleRegistered,
                FleetAudit.VehicleEntity,
                vehicle.Id.ToString(),
                null,
                cancellationToken)
            .ConfigureAwait(false);

        return Result<VehicleView>.Success(VehicleView.From(vehicle));
    }
}
