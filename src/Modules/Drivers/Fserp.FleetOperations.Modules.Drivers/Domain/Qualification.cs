using Fserp.FleetOperations.Modules.Fleet.Contracts;
using MPCore.Domain.Model;

namespace Fserp.FleetOperations.Modules.Drivers.Domain;

/// <summary>
/// One vehicle type a driver may operate. A qualification is the vehicle type only: no licence class and
/// no expiry (D-4).
/// </summary>
/// <remarks>
/// A child entity of <see cref="Driver"/> rather than a value in a list, as the plan specifies, so it has
/// an identity of its own in <c>drivers.qualifications</c>. It is created only by
/// <see cref="Driver.Register"/>; qualifications are set at registration and are not edited in this scope
/// (D-2).
/// </remarks>
public sealed class Qualification : Entity<Guid>
{
    // For Entity Framework materialization only.
    private Qualification()
    {
    }

    internal Qualification(Guid id, VehicleType vehicleType)
        : base(id)
    {
        VehicleType = vehicleType;
    }

    /// <summary>The vehicle type, from the closed list in Fleet.Contracts (X-5).</summary>
    public VehicleType VehicleType { get; private set; }
}
