using Fserp.FleetOperations.Modules.Drivers.Application.Ports;
using Fserp.FleetOperations.Modules.Drivers.Application.Views;
using Fserp.FleetOperations.Modules.Drivers.Domain;
using Fserp.FleetOperations.Modules.Fleet.Contracts;
using MPCore.Application.Messaging;
using MPCore.Application.Results;
using MPCore.Application.Time;
using MPCore.Audit;
using MPCore.Persistence.Abstractions;

namespace Fserp.FleetOperations.Modules.Drivers.Application.Commands;

/// <summary>
/// Registers a driver. The driver starts Active, with one qualification per vehicle type given; at least
/// one is required (D-5) and each may appear once.
/// </summary>
/// <param name="FullName">The full name as entered; normalized by trimming.</param>
/// <param name="VehicleTypes">The vehicle types the driver may operate.</param>
public sealed record RegisterDriver(string FullName, IReadOnlyList<VehicleType> VehicleTypes)
    : ICommand<Result<DriverView>>;

/// <summary>Handles <see cref="RegisterDriver"/>. One transaction, owned by the middleware.</summary>
public static class RegisterDriverHandler
{
    /// <summary>
    /// Registers the driver. A name or a qualification list the validator did not refuse is still checked
    /// by the value object and the aggregate, which answer <c>422</c> with the broken rule's own code.
    /// </summary>
    /// <param name="command">The command.</param>
    /// <param name="drivers">The driver repository.</param>
    /// <param name="unitOfWork">The unit of work the middleware commits; declared so the handler runs in its transaction.</param>
    /// <param name="clock">The clock the identities' timestamps come from.</param>
    /// <param name="audit">The business audit recorder.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <remarks>
    /// There is no uniqueness pre-check: the plan records no uniqueness rule for a driver's name, and two
    /// people may share one. The only database constraint is the per-driver qualification index (L-18),
    /// which the duplicate rule refuses first.
    /// </remarks>
    public static async Task<Result<DriverView>> Handle(
        RegisterDriver command,
        IDriverRepository drivers,
        IUnitOfWork unitOfWork,
        IClock clock,
        IBusinessAuditRecorder audit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(drivers);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(audit);

        // Validate first: the value object and the aggregate check their own rules before anything is
        // tracked, so a refusal leaves nothing pending for the middleware to commit.
        var fullName = DriverName.Create(command.FullName);
        var driver = Driver.Register(
            Guid.CreateVersion7(clock.UtcNow),
            fullName,
            command.VehicleTypes ?? [],
            clock.UtcNow);

        // Mutate second.
        drivers.Add(driver);
        await audit.RecordAsync(
                DriversAudit.Module,
                DriversAudit.DriverRegistered,
                DriversAudit.DriverEntity,
                driver.Id.ToString(),
                null,
                cancellationToken)
            .ConfigureAwait(false);

        return Result<DriverView>.Success(DriverView.From(driver));
    }
}
