using FluentValidation;
using Fserp.FleetOperations.Modules.Fleet.Application.Queries;

namespace Fserp.FleetOperations.Modules.Fleet.Application.Validators;

/// <summary>Input shape of <see cref="GetVehicle"/>: a non-empty identity (400).</summary>
public sealed class GetVehicleValidator : AbstractValidator<GetVehicle>
{
    /// <summary>Declares the rules.</summary>
    public GetVehicleValidator()
    {
        RuleFor(query => query.VehicleId).NotEmpty();
    }
}
