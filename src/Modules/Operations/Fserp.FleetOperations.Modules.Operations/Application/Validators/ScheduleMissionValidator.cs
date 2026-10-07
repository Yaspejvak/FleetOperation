using FluentValidation;
using Fserp.FleetOperations.Modules.Operations.Application.Commands;

namespace Fserp.FleetOperations.Modules.Operations.Application.Validators;

/// <summary>
/// Input shape of <see cref="ScheduleMission"/>: a non-empty mission identity and a scheduled time that
/// was actually given (400).
/// </summary>
/// <remarks>
/// The scheduled time is required, and that is all. There is deliberately no "must be in the future"
/// rule (O-2): the plan asked the question without proposing one, and the decided default is no rule.
/// A default <see cref="DateTimeOffset"/> is refused as "not given", not as "in the past".
/// </remarks>
public sealed class ScheduleMissionValidator : AbstractValidator<ScheduleMission>
{
    /// <summary>Declares the rules.</summary>
    public ScheduleMissionValidator()
    {
        RuleFor(command => command.MissionId).NotEmpty();
        RuleFor(command => command.ScheduledAt).NotEmpty();
    }
}
