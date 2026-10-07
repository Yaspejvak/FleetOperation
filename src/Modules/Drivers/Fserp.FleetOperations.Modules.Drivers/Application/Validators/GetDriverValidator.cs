using FluentValidation;
using Fserp.FleetOperations.Modules.Drivers.Application.Queries;

namespace Fserp.FleetOperations.Modules.Drivers.Application.Validators;

/// <summary>Input shape of <see cref="GetDriver"/>: a non-empty identity (400).</summary>
public sealed class GetDriverValidator : AbstractValidator<GetDriver>
{
    /// <summary>Declares the rules.</summary>
    public GetDriverValidator()
    {
        RuleFor(query => query.DriverId).NotEmpty();
    }
}
