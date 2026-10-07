using FluentValidation;
using Fserp.FleetOperations.Modules.Operations.Application.Queries;

namespace Fserp.FleetOperations.Modules.Operations.Application.Validators;

/// <summary>
/// Input shape of <see cref="GetMission"/>: a non-empty identity (400). A gRPC <c>mission_id</c> that is
/// not a UUID parses to <see cref="Guid.Empty"/>, which this refuses, so an unparsable id is a validation
/// failure rather than a fabricated code (L-7).
/// </summary>
public sealed class GetMissionValidator : AbstractValidator<GetMission>
{
    /// <summary>Declares the rules.</summary>
    public GetMissionValidator()
    {
        RuleFor(query => query.MissionId).NotEmpty();
    }
}
