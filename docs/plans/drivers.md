# Drivers: plan note

Status: approved. Challenge sections 5, 7, 11.

**Responsibility.** Owns the drivers: their name, operational status, qualifications (which vehicle types
they may operate) and whether a driver is currently committed to a mission. Answers "may this driver take
this mission with a vehicle of this type?" for Operations. Knows nothing about vehicles beyond the
`VehicleType` list, and nothing about missions beyond an id.

Project: `src/Modules/Drivers/Fserp.FleetOperations.Modules.Drivers` and
`src/Modules/Drivers/Fserp.FleetOperations.Modules.Drivers.Contracts`. Error domain `drivers`. Audit
module name `Drivers`.

## Aggregate: `Driver` (`AggregateRoot<Guid>`)

| Member | Type | Note |
|---|---|---|
| `Id` | `Guid` (v7) | identity |
| `FullName` | value object `DriverName` | the only profile field (D-1) |
| `OperationalStatus` | enum `Active`, `Inactive` | `Active` on registration |
| `Qualifications` | child entities `Qualification` (`Entity<Guid>`) | one per `VehicleType` the driver may operate; at least one (D-5) |
| `CommittedMissionId` | `Guid?` | set while a mission in `Assigned` or `InProgress` holds the driver |
| concurrency token | PostgreSQL `xmin` | Infrastructure only |

`Qualification` holds a `Fleet.Contracts.VehicleType` and nothing else (D-4). Drivers references
`Fleet.Contracts` for that enum only; it never uses Fleet's readers or commitment ports.

**Available**: `OperationalStatus == Active && CommittedMissionId == null`.

## Invariants

| Code | Checked in | Broken when |
|---|---|---|
| `DRIVER_NAME_REQUIRED` | `DriverName` value object | empty name |
| `DRIVER_QUALIFICATION_REQUIRED` | `Driver.Register` (D-5) | no qualification |
| `DRIVER_QUALIFICATION_DUPLICATE` | `Driver.Register` | same vehicle type twice |
| `DRIVER_HAS_MISSION_COMMITMENT` | `ChangeStatus(Inactive)` (D-2) | `CommittedMissionId != null` |
| `DRIVER_NOT_ACTIVE` | `CommitToMission` (assignment precondition 6) | `OperationalStatus != Active` |
| `DRIVER_NOT_AVAILABLE` | `CommitToMission` (precondition 7) | committed to a different mission |
| `DRIVER_NOT_QUALIFIED` | `CommitToMission` (precondition 8) | no qualification for the vehicle type given |
| `DRIVER_NOT_COMMITTED_TO_MISSION` | `ReleaseFromMission` | not committed to the mission named |

Qualification is checked by `Driver` because the qualifications are the driver's state and are covered by
the driver's concurrency token. `ChangeStatus` to the current status is a no-op, as in Fleet.

## Commands (`ICommand<Result<DriverView>>`)

| Command | Endpoint | Audit action |
|---|---|---|
| `RegisterDriver(fullName, vehicleTypes[])` | `POST /api/drivers` | `DriverRegistered` (recommended) |
| `ChangeDriverStatus(driverId, status)` | `PUT /api/drivers/{driverId}/status` | `DriverStatusChanged` (recommended); rejected attempt recorded |

Not on the bus: `CommitToMission(missionId, vehicleType)` and `ReleaseFromMission(missionId)` through
`IDriverCommitments`, implemented in `Application/Contracts/`, called inside the Operations transaction,
no save of their own.

No other management operation (D-2): qualifications are set at registration and not edited in this scope.

## Queries

| Query | REST | gRPC | Cached |
|---|---|---|---|
| `GetDriver(driverId)` -> `DriverView` | `GET /api/drivers/{driverId:guid}` | none | no |
| `GetAvailableDrivers()` -> `IReadOnlyList<AvailableDriverView>` | `GET /api/drivers/available` | none | no |

`GET /api/drivers/{driverId}` is added for the `201 Location` header. The module prefix and the resource
coincide (`/api/drivers`), deliberately. Get Available Drivers has no vehicle-type filter (D-6).

## Contracts published (`Drivers.Contracts`, present in the skeleton)

```text
DriverSnapshot                    record (DriverId, IsActive, QualifiedVehicleTypes, CommittedMissionId)

IDriverEligibilityReader          read-only
  GetAsync(driverId) -> DriverSnapshot?

IDriverCommitments                WRITES - same recorded reason as IVehicleCommitments (decision 1)
  CommitToMissionAsync(driverId, missionId, vehicleType)
  ReleaseFromMissionAsync(driverId, missionId)
```

## Domain events (in-process)

`DriverRegistered`, `DriverStatusChanged`, `DriverCommittedToMission`, `DriverReleasedFromMission`. No
handler in this scope (nothing is cached for drivers). An unrouted event is dropped with an informational
log; that is accepted and must not be counted as tested behaviour.

## Authorization

| Operation | Policy |
|---|---|
| Register Driver, Change Driver Status | `FleetManager` (D-3) |
| Get Driver, Get Available Drivers | `OperationalReader` |

## Business audit

Not in the challenge's minimum list. Recommended: `DriverRegistered` and `DriverStatusChanged` business
actions; rejected `ChangeDriverStatus` attempts; entity change policy on `OperationalStatus`,
`CommittedMissionId` and the qualification types. `FullName` is personal data: it is masked with
`Redact` if included at all.

## Persistence

Schema `drivers`, tables `drivers` and `qualifications`; `xmin` on `drivers`; enums stored as strings.

## Dependencies

`Fleet.Contracts` for `VehicleType` only. Used by Operations through `Drivers.Contracts`.

## Decisions on former open questions

- **D-1 Decided (owner).** Full name only.
- **D-2 Decided (owner).** Register Driver and Change Driver Status (`Active`, `Inactive`) only. A committed
  driver cannot be deactivated (`DRIVER_HAS_MISSION_COMMITMENT`), same rule as F-3.
- **D-3 Decided (owner).** `FleetManager` registers and manages drivers.
- **D-4 Decided (default).** A qualification is the vehicle type only; no licence class, no expiry.
- **D-5 Decided (owner).** At least one qualification at registration.
- **D-6 Decided (default).** Get Available Drivers has no filter.

### Decided (lead), round 5

- **L-16.** `DriverView(Id, FullName, OperationalStatus, QualifiedVehicleTypes, CommittedMissionId)` and
  `AvailableDriverView(Id, FullName, QualifiedVehicleTypes)`. The plan names the views, not their fields;
  these mirror `VehicleView` and `AvailableVehicleView`.
- **L-17.** A full name is capped at 128 characters on the trimmed value, enforced by the
  `RegisterDriver` validator (`400`, field `full_name`) and by `full_name character varying(128)`.
  **No `DRIVER_NAME_TOO_LONG` business rule was added**: the plan lists no such invariant, and inventing
  one would be inventing a rule. `DRIVER_NAME_REQUIRED` remains the only name rule in the domain. This
  differs deliberately from the plate number, where the 16-character bound is an owner decision (F-11)
  carrying its own rule.
- **L-18.** `Qualification : Entity<Guid>` with a version 7 id, stored in `drivers.qualifications` with a
  unique index on `(driver_id, vehicle_type)`, so `DRIVER_QUALIFICATION_DUPLICATE` has a database
  backstop exactly as the unique plate index backstops F-2.
- **L-19.** `Driver.Register` checks `DRIVER_QUALIFICATION_REQUIRED` before
  `DRIVER_QUALIFICATION_DUPLICATE`. An empty list cannot contain a duplicate, so the order is
  unobservable and is fixed only so code and tests cannot drift (same reasoning as L-5).
- **L-20.** `Driver.CommitToMission` for the mission the driver already holds is a **no-op with no
  event**, matching `Vehicle.CommitToMission`. This is not an invention: `DRIVER_NOT_AVAILABLE` is
  worded "committed to a **different** mission", so the same-mission call is outside the rule, and this
  note already mirrors Fleet's repeat-operation semantics for `ChangeStatus`. `ReleaseFromMission` is
  **not** symmetrical — a driver holding no mission, or a different one, is refused, because silently
  accepting would let one mission release a driver another mission holds.
- **L-21.** `IDriverCommitments` and `IVehicleCommitments` throw `ResultFailureException` carrying their
  own module's existing `DRIVER_NOT_FOUND` / `VEHICLE_NOT_FOUND` descriptor when the id is unknown. The
  ports return `Task` and no plan names an outcome; reusing the module's own not-found failure means the
  edge maps it to the same `404` the commands produce and no new code was invented. **Round 7's
  `AssignMission` depends on this**, so it is recorded rather than left as a handler detail.
