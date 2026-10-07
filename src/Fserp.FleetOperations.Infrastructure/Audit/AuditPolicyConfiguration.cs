using Fserp.FleetOperations.Modules.Drivers.Application;
using Fserp.FleetOperations.Modules.Drivers.Domain;
using Fserp.FleetOperations.Modules.Fleet.Application;
using Fserp.FleetOperations.Modules.Fleet.Domain;
using Fserp.FleetOperations.Modules.Operations.Application;
using Fserp.FleetOperations.Modules.Operations.Domain;
using MPCore.Audit;

namespace Fserp.FleetOperations.Infrastructure.Audit;

/// <summary>
/// Declares which entities and which of their properties the audit trail records. Default deny:
/// an entity that is not declared here leaves no trace, and a property that is not included is
/// not captured. Credential-like properties can never be included; banking and identity
/// identifiers are always masked.
/// </summary>
public static class AuditPolicyConfiguration
{
    public static void Configure(AuditPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        // Fleet (docs/plans/fleet.md, "Business audit"). The plate number is recorded verbatim by the
        // owner's decision F-2: it identifies a vehicle, not a person.
        policy.Entity<Vehicle>(FleetAudit.Module)
            .Include(vehicle => vehicle.PlateNumber)
            .Include(vehicle => vehicle.VehicleType)
            .Include(vehicle => vehicle.Capacity)
            .Include(vehicle => vehicle.OperationalStatus)
            .Include(vehicle => vehicle.MaintenanceStatus)
            .Include(vehicle => vehicle.CommittedMissionId);

        // Drivers (docs/plans/drivers.md, "Business audit"). FullName is personal data, so it is masked
        // with Redact: the trail records that the name changed, never the name. The plan allows leaving it
        // out entirely; it is included masked so a correction of a misspelled name is still accountable.
        policy.Entity<Driver>(DriversAudit.Module)
            .Mask(driver => driver.FullName, MaskStyle.Redact)
            .Include(driver => driver.OperationalStatus)
            .Include(driver => driver.CommittedMissionId);

        // The qualification types the plan names. They live in child rows, and the audit policy captures
        // properties of a declared entity, so Qualification is declared under the same module name: the
        // trail then carries one Created row per qualification with its vehicle type. A qualification
        // carries nothing else (D-4), so there is nothing here that could identify a person.
        policy.Entity<Qualification>(DriversAudit.Module)
            .Include(qualification => qualification.VehicleType);

        // Operations (docs/plans/operations.md, "Business audit"). Every property the plan's entity change
        // policy names, all free text or ids and no personal data, so nothing here is masked: a location is
        // a place, not a person, and the two assigned ids name a vehicle and a driver record.
        policy.Entity<Mission>(OperationsAudit.Module)
            .Include(mission => mission.Status)
            .Include(mission => mission.ScheduledAt)
            .Include(mission => mission.AssignedVehicleId)
            .Include(mission => mission.AssignedDriverId)
            .Include(mission => mission.RequiredCapacity)
            .Include(mission => mission.Origin)
            .Include(mission => mission.Destination);

        // Business actions and their outcomes are recorded from handlers through
        // IBusinessAuditRecorder; rejected attempts are written detached from the transaction.
    }
}
