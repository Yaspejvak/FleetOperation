using Fserp.FleetOperations.Modules.Operations.Application.Ports;
using Fserp.FleetOperations.Modules.Operations.Application.Views;
using Fserp.FleetOperations.Modules.Operations.Domain;
using MPCore.Application.Messaging;
using MPCore.Application.Results;
using MPCore.Application.Time;
using MPCore.Audit;
using MPCore.Persistence.Abstractions;

namespace Fserp.FleetOperations.Modules.Operations.Application.Commands;

/// <summary>
/// Creates a mission in <c>Draft</c>. The scheduled time is not given here: it is fixed by
/// <see cref="ScheduleMission"/> and never changed (O-1).
/// </summary>
/// <param name="Origin">Where the mission starts; non-empty after trimming.</param>
/// <param name="Destination">Where the mission ends; it may equal <paramref name="Origin"/> (O-7).</param>
/// <param name="RequiredCapacityKg">The load the mission requires, in kilograms; positive (X-4).</param>
public sealed record CreateMission(string Origin, string Destination, decimal RequiredCapacityKg)
    : ICommand<Result<MissionView>>;

/// <summary>Handles <see cref="CreateMission"/>. One transaction, owned by the middleware.</summary>
public static class CreateMissionHandler
{
    /// <summary>
    /// Creates the mission. The two value objects check their own rules before anything is tracked, so a
    /// refusal leaves nothing pending and reaches the caller as <c>422</c> under the rule's own code.
    /// </summary>
    /// <param name="command">The command.</param>
    /// <param name="missions">The mission repository.</param>
    /// <param name="unitOfWork">The unit of work the middleware commits; declared so the handler runs in its transaction.</param>
    /// <param name="clock">The clock the identity's timestamp comes from.</param>
    /// <param name="audit">The business audit recorder.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public static async Task<Result<MissionView>> Handle(
        CreateMission command,
        IMissionRepository missions,
        IUnitOfWork unitOfWork,
        IClock clock,
        IBusinessAuditRecorder audit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(missions);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(audit);

        // Validate first: nothing is tracked until all three values have passed their rules.
        var origin = Location.Create(command.Origin);
        var destination = Location.Create(command.Destination);
        var requiredCapacity = RequiredCapacity.FromKilograms(command.RequiredCapacityKg);

        // Mutate second.
        var mission = Mission.Create(Guid.CreateVersion7(clock.UtcNow), origin, destination, requiredCapacity);
        missions.Add(mission);
        await audit.RecordAsync(
                OperationsAudit.Module,
                OperationsAudit.MissionCreated,
                OperationsAudit.MissionEntity,
                mission.Id.ToString(),
                null,
                cancellationToken)
            .ConfigureAwait(false);

        return Result<MissionView>.Success(MissionView.From(mission));
    }
}
