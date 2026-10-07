using Fserp.FleetOperations.Modules.Drivers.Application.Ports;
using Fserp.FleetOperations.Modules.Drivers.Application.Views;
using Fserp.FleetOperations.Modules.Drivers.Domain;
using MPCore.Application.Messaging;
using MPCore.Application.Results;
using MPCore.Audit;
using MPCore.Domain.Rules;
using MPCore.Persistence.Abstractions;

namespace Fserp.FleetOperations.Modules.Drivers.Application.Commands;

/// <summary>
/// Sets a driver's operational status to Active or Inactive. There is no other management operation in
/// this scope: qualifications are set at registration and not edited (D-2).
/// </summary>
/// <param name="DriverId">The driver's identity.</param>
/// <param name="Status">The requested operational status.</param>
public sealed record ChangeDriverStatus(Guid DriverId, OperationalStatus Status)
    : ICommand<Result<DriverView>>;

/// <summary>Handles <see cref="ChangeDriverStatus"/>. One transaction, owned by the middleware.</summary>
public static class ChangeDriverStatusHandler
{
    /// <summary>
    /// Changes the status. Unknown driver: <c>404</c>. Same status: <c>200</c>, no event, no audit. A
    /// committed driver set Inactive: <c>422 DRIVER_HAS_MISSION_COMMITMENT</c>, with the rejected attempt
    /// recorded detached so it survives the rollback (D-2).
    /// </summary>
    /// <param name="command">The command.</param>
    /// <param name="drivers">The driver repository.</param>
    /// <param name="unitOfWork">The unit of work the middleware commits; declared so the handler runs in its transaction.</param>
    /// <param name="audit">The business audit recorder.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <remarks>
    /// The metadata carries the two statuses and nothing else. The driver's name is personal data and is
    /// never put in an audit metadata value, which the policy could not mask.
    /// </remarks>
    public static async Task<Result<DriverView>> Handle(
        ChangeDriverStatus command,
        IDriverRepository drivers,
        IUnitOfWork unitOfWork,
        IBusinessAuditRecorder audit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(drivers);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(audit);

        var driver = await drivers.GetAsync(command.DriverId, cancellationToken).ConfigureAwait(false);
        if (driver is null)
        {
            return Result<DriverView>.FromFailure(DriversFailures.DriverNotFound());
        }

        var from = driver.OperationalStatus;
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [DriversAudit.FromMetadata] = from.ToString(),
            [DriversAudit.ToMetadata] = command.Status.ToString(),
        };

        bool changed;
        try
        {
            // The aggregate checks its rule before it changes anything, so a refusal leaves nothing pending.
            changed = driver.ChangeStatus(command.Status);
        }
        catch (BusinessRuleValidationException rejected)
        {
            await audit.RecordAttemptAsync(
                    DriversAudit.Module,
                    DriversAudit.DriverStatusChanged,
                    AuditOutcome.Rejected,
                    new AuditFailure(rejected.Rule.ErrorDomain, rejected.Rule.Code),
                    null,
                    DriversAudit.DriverEntity,
                    driver.Id.ToString(),
                    metadata,
                    cancellationToken)
                .ConfigureAwait(false);
            throw;
        }

        if (changed)
        {
            await audit.RecordAsync(
                    DriversAudit.Module,
                    DriversAudit.DriverStatusChanged,
                    DriversAudit.DriverEntity,
                    driver.Id.ToString(),
                    metadata,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return Result<DriverView>.Success(DriverView.From(driver));
    }
}
