# Fleet: plan note

Status: approved. Challenge sections 4, 8, 9, 11, 12.

**Responsibility.** Owns the vehicles: their registration, operational status, maintenance status and
whether a vehicle is currently committed to a mission. Answers "is this vehicle usable?" for Operations
and serves the cached available-vehicles list. Owns the vehicle-type vocabulary. Knows nothing about the
mission lifecycle; it knows only the id of the mission a vehicle is committed to.

Projects: `src/Modules/Fleet/Fserp.FleetOperations.Modules.Fleet` and
`src/Modules/Fleet/Fserp.FleetOperations.Modules.Fleet.Contracts`. Error domain `fleet`. Audit module
name `Fleet`.

## Aggregate: `Vehicle` (`AggregateRoot<Guid>`)

| Member | Type | Note |
|---|---|---|
| `Id` | `Guid` (v7) | identity |
| `PlateNumber` | value object | non-empty after normalization (trimmed, upper case); no format rule; unique across the fleet (F-2) |
| `VehicleType` | `Fleet.Contracts.VehicleType` | `Van`, `Truck`, `HeavyTruck` (X-5) |
| `Capacity` | value object | kilograms, positive decimal (X-4) |
| `OperationalStatus` | enum `Active`, `Inactive` | changed by Change Status; `Active` on registration (F-5) |
| `MaintenanceStatus` | enum `NotUnderMaintenance`, `UnderMaintenance` | changed only by Start/Complete Maintenance |
| `CommittedMissionId` | `Guid?` | set while a mission in `Assigned` or `InProgress` holds the vehicle |
| concurrency token | PostgreSQL `xmin` | mapped in Infrastructure, never visible in Domain |

Two independent status fields (F-1). The displayed status is derived: `UnderMaintenance` wins, otherwise
the operational status. Complete Maintenance changes only the maintenance field.

**Available** (one definition, used by the cached list and by the commit rule):
`OperationalStatus == Active && MaintenanceStatus == NotUnderMaintenance && CommittedMissionId == null`.

No child entity for maintenance: maintenance carries no data (F-4); the history is in the audit trail.

## Invariants (each a `BusinessRule` checked by `Vehicle` or its value objects)

| Code | Checked in | Broken when |
|---|---|---|
| `VEHICLE_CAPACITY_MUST_BE_POSITIVE` | `Capacity` value object | capacity <= 0 kg |
| `VEHICLE_PLATE_NUMBER_REQUIRED` | `PlateNumber` value object | empty after normalization |
| `VEHICLE_PLATE_NUMBER_TOO_LONG` | `PlateNumber` value object (owner, F-11) | longer than 16 characters after normalization |
| `VEHICLE_PLATE_NUMBER_ALREADY_REGISTERED` | unique index `fleet.vehicles(plate_number)`, surfaced as `409` by `RegisterVehicle` | another vehicle already has this plate (F-2) |
| `VEHICLE_ALREADY_UNDER_MAINTENANCE` | `StartMaintenance` | already `UnderMaintenance` |
| `VEHICLE_HAS_MISSION_COMMITMENT` | `StartMaintenance` (decision 3) and `ChangeStatus(Inactive)` (F-3) | `CommittedMissionId != null` |
| `VEHICLE_NOT_UNDER_MAINTENANCE` | `CompleteMaintenance` | not `UnderMaintenance` |
| `VEHICLE_NOT_ACTIVE` | `CommitToMission` (assignment precondition 2) | `OperationalStatus != Active` |
| `VEHICLE_UNDER_MAINTENANCE` | `CommitToMission` (precondition 3) | `UnderMaintenance` |
| `VEHICLE_CAPACITY_INSUFFICIENT` | `CommitToMission` (precondition 4) | `Capacity < requiredCapacityKg` |
| `VEHICLE_NOT_AVAILABLE` | `CommitToMission` (precondition 5) | committed to a different mission |
| `VEHICLE_NOT_COMMITTED_TO_MISSION` | `ReleaseFromMission` | not committed to the mission named |

Failure codes that are not `BusinessRule`s (failure descriptors in `Application/FleetFailures.cs`, error
domain `fleet`, recorded by the lead when round 1 closed):

| Code | Returned by | Meaning | REST | gRPC |
|---|---|---|---|---|
| `VEHICLE_NOT_FOUND` | every command and query that names a vehicle id (`ChangeVehicleStatus`, `GetVehicle`, later the maintenance commands) | no vehicle has the requested id | `404` | `NotFound` |

Host-wide failure, not Fleet's (`src/Fserp.FleetOperations.Api/Hosting/HostFailures.cs`, text in
`Api/Resources/HostMessages.resx`): `fleetoperations/CONCURRENCY_CONFLICT`, key
`fleetoperations.concurrency_conflict`, category `Concurrency`, REST `409`. It is produced by
`ConcurrencyExceptionMapper` for a `DbUpdateConcurrencyException` (lost `xmin` race) on any module's
aggregate. See L-1.

`CommitToMission` for the mission the vehicle is already committed to is a no-op (no event).
`ChangeStatus` to the status the vehicle already has is a no-op: `200`, no event, no audit (F-8).

## Commands (`ICommand<Result<VehicleView>>`)

| Command | Endpoint | Aggregate method | Audit action |
|---|---|---|---|
| `RegisterVehicle(plate, type, capacityKg)` | `POST /api/fleet/vehicles` | `Vehicle.Register` (starts `Active`, `NotUnderMaintenance`) | `VehicleRegistered` (recommended) |
| `ChangeVehicleStatus(vehicleId, status)` | `PUT /api/fleet/vehicles/{vehicleId}/status` | `Vehicle.ChangeStatus` | `VehicleStatusChanged` (required); rejected attempt recorded |
| `StartMaintenance(vehicleId)` | `POST /api/fleet/vehicles/{vehicleId}/maintenance/start` | `Vehicle.StartMaintenance` | `MaintenanceStarted` (required); rejected attempt recorded |
| `CompleteMaintenance(vehicleId)` | `POST /api/fleet/vehicles/{vehicleId}/maintenance/complete` | `Vehicle.CompleteMaintenance` | `MaintenanceCompleted` (required) |

`ChangeVehicleStatus` accepts `Active` or `Inactive` only; maintenance is entered and left only through
the maintenance commands.

Not commands on the bus: `CommitToMission` and `ReleaseFromMission` are called by Operations, inside the
Operations command's transaction, through `IVehicleCommitments` (below). They are implemented in
`Application/Contracts/`, load the aggregate through `IVehicleRepository`, call the aggregate method and
do not save; the caller's unit of work commits.

## Queries (`IQuery<T>`, read-model port, no unit of work)

| Query | REST | gRPC | Cached |
|---|---|---|---|
| `GetVehicle(vehicleId)` -> `VehicleView` | `GET /api/fleet/vehicles/{vehicleId:guid}` | `VehicleService.GetVehicle` | no |
| `GetAvailableVehicles()` -> `IReadOnlyList<AvailableVehicleView>` | `GET /api/fleet/vehicles/available` | `VehicleService.GetAvailableVehicles` | **yes** |

`GET /vehicles/{vehicleId}` is added for the `201 Location` header and for REST/gRPC parity of
`GetVehicle`. The `:guid` route constraint keeps it from matching `/vehicles/available`. No filters and no
paging on the available list (F-7).

Views: `VehicleView(Id, PlateNumber, VehicleType, CapacityKg, OperationalStatus, MaintenanceStatus,
DisplayStatus, CommittedMissionId)`; `AvailableVehicleView(Id, PlateNumber, VehicleType, CapacityKg)`.

REST status codes: `201` register; `200` others; `400` validation; `404` unknown vehicle; `409` lost
concurrency; `422` broken rule; `401`/`403`.

## Contracts published (`Fleet.Contracts`, present in the skeleton)

```text
VehicleType                       enum Van, Truck, HeavyTruck - the one list (X-5)
VehicleSnapshot                   record (VehicleId, VehicleType, CapacityKg, IsActive, IsUnderMaintenance, CommittedMissionId)

IVehicleAvailabilityReader        read-only
  GetAsync(vehicleId) -> VehicleSnapshot?

IVehicleCommitments               WRITES - reason recorded on the interface (decision 1)
  CommitToMissionAsync(vehicleId, missionId, requiredCapacityKg)
  ReleaseFromMissionAsync(vehicleId, missionId)
```

The snapshot is read from PostgreSQL, never from the cache. The implementations go in
`Application/Contracts/` (rounds 1 to 3) and are registered by type in `AddFleetModule<TContext>`.

## Domain events (in-process; delivered after commit on the Wolverine durable local queue)

`VehicleRegistered`, `VehicleStatusChanged`, `MaintenanceStarted`, `MaintenanceCompleted`,
`VehicleCommittedToMission`, `VehicleReleasedFromMission`.

Handlers in `Application/Events/`: `AvailableVehiclesCacheEvictionHandler`, one `Handle` per event above,
each calling `ICache.RemoveAsync(<available key>)`. Removal is idempotent, so at-least-once delivery is
harmless.

## Cache (decision 4)

- **What**: the result of `GetAvailableVehicles`, the whole list, one entry.
- **Key**: `fleet:vehicles:available:v1`, qualified by `MPCoreCacheOptions.KeyPrefix`. `v1` is the shape
  version of `AvailableVehicleView`; bump it when the view changes. One constant in the module, used by
  the query handler and the eviction handler.
- **Read**: `IReadThroughCache.GetOrCreateAsync(key, factory, expiration)`; one factory per key per
  instance under concurrent misses.
- **Expiration**: 30 seconds absolute, from module options (F-6).
- **Not cached**: `GetVehicle`, `IVehicleAvailabilityReader`, anything a command decides on.

## Authorization (decision 5)

| Operation | Policy |
|---|---|
| Register, Change Status, Start/Complete Maintenance | `FleetManager` |
| Get Vehicle, Get Available Vehicles (REST and gRPC) | `OperationalReader` (Operators and Fleet Managers) |

## Business audit

- Business actions through `IBusinessAuditRecorder.RecordAsync` (module `Fleet`, entity `Vehicle`, id):
  `VehicleStatusChanged` (metadata: from, to), `MaintenanceStarted`, `MaintenanceCompleted`;
  `VehicleRegistered` recommended.
- Rejected attempts through `RecordAttemptAsync(Rejected, failure)`: `StartMaintenance` refused with
  `VEHICLE_HAS_MISSION_COMMITMENT` or `VEHICLE_ALREADY_UNDER_MAINTENANCE`; `ChangeVehicleStatus` refused
  with `VEHICLE_HAS_MISSION_COMMITMENT`.
- Entity change policy: `PlateNumber` (not masked, F-2), `VehicleType`, `Capacity`, `OperationalStatus`,
  `MaintenanceStatus`, `CommittedMissionId`.

## Persistence

Schema `fleet`, table `vehicles`; `xmin` concurrency token; status and type columns stored as strings.
Unique index on `plate_number` (F-2); a violation maps to `409 VEHICLE_PLATE_NUMBER_ALREADY_REGISTERED`.

## Dependencies

None on other modules. Used by Operations (readers and commitments) and Drivers (`VehicleType` only)
through `Fleet.Contracts`.

## Decisions on former open questions

- **F-1 Decided (owner).** Two independent fields. Complete Maintenance changes only the maintenance
  field; the operational status is untouched.
- **F-2 Decided (owner).** Plate numbers are unique: unique index on `fleet.vehicles(plate_number)`,
  violation mapped to `409`. No format rule. Not masked in the audit trail.
- **F-3 Decided (owner).** Setting a committed vehicle to `Inactive` is refused with
  `VEHICLE_HAS_MISSION_COMMITMENT`.
- **F-4 Decided (default).** An `Inactive` vehicle may start maintenance. Maintenance carries no data.
- **F-5 Decided (owner).** `Active` on registration.
- **F-6 Decided (default).** Up to 30 s of staleness is accepted.
- **F-7 Decided (default).** No filters and no paging on `GET /vehicles/available`. If filters come
  later, filter the one cached list in memory rather than adding keys.
- **F-8 Decided (default).** Change Status to the current status is a no-op `200`, no audit.

## Decisions recorded when round 1 closed

- **F-9 Decided (owner), cross-module REST contract.** Every REST body of every module names enum
  members as strings (`Van`, `Active`, `NotUnderMaintenance`, ...), in requests and responses. One
  host-wide setting does it: `ConfigureHttpJsonOptions` with
  `JsonStringEnumConverter(allowIntegerValues: false)` in `Program.cs`, so a number for an enum is
  refused, never bound. It applies to Drivers, Operations and Administration as they gain endpoints;
  no module adds its own enum converter. gRPC is unaffected (proto enums travel as numbers by design).
- **F-10 Decided (owner), round 4 item.** The host's HTTP exception mappers
  (`UniqueViolationExceptionMapper`, and the concurrency mapper added when round 1 closed) have no gRPC
  twin yet. Round 4, which adds the first Fleet gRPC service, adds an `IGrpcExceptionMapper` for each,
  mapping to the same failure descriptors, so a unique violation and a lost concurrency race answer the
  same failure on both transports. Until then no Fleet gRPC method exists, so nothing is exposed.
- **F-11 Decided (owner).** A plate number is at most 16 characters, counted on the normalized value
  (trimmed, upper case). It is enforced by `PlateNumber.Create` as the `BusinessRule`
  `VEHICLE_PLATE_NUMBER_TOO_LONG` (`fleet.vehicle_plate_number_too_long`), and by the column
  `plate_number character varying(16)` (migration `20261005120646_LimitVehiclePlateNumberLength`).
  A new migration was added instead of editing the initial one, because a database that has already
  applied `InitialFleetVehicles` would never pick up an edited copy. The bound also keeps the unique
  btree index far below PostgreSQL's index-row limit.
- **F-12 Decided (owner), outcome.** A REST body that cannot be read (a number for an enum, an unknown
  enum name, an empty body) answers `400` `application/problem+json` with MP Core's own
  `mpcore.http/MALFORMED_REQUEST` (category `Validation`, detail "The request is malformed."). It is
  host wiring of one statement: `RouteHandlerOptions.ThrowOnBadRequest = true`. Known limitation: the
  answer carries no per-field violation (no `vehicle_type` field path), unlike the validator's
  `VALIDATION_FAILED`. Building one from the JSON parser's error path was refused as more than the
  twenty-line budget and fragile.

### Decided (lead)

- **L-1.** Lost optimistic concurrency maps to `fleetoperations/CONCURRENCY_CONFLICT`, category
  `Concurrency`, `409`. MP Core 0.9.3 has `ErrorCategory.Concurrency` but no concurrency code or
  descriptor; a scan of its assemblies' UTF-16 strings found only the `mpcore.http` codes
  `UNEXPECTED_FAILURE`, `MALFORMED_REQUEST`, `BUSINESS_RULE`, `FORBIDDEN`, `UNAUTHENTICATED` and
  `REQUEST_CANCELLED`. The failure is host-wide, not under `fleet`, because a lost race can happen on
  any module's aggregate and the mapper does not read the exception's entries. It is replaced if round 7
  (O-9) finds a code MP Core produces itself.
- **L-2.** `RegisterVehicleValidator` repeats `VEHICLE_PLATE_NUMBER_TOO_LONG` with the same code and
  key, measured on `PlateNumber.Normalize`, as it already does for `VEHICLE_PLATE_NUMBER_REQUIRED`. A
  too-long plate is `400` with field `plate_number`; the value object's `422` is the enforcement when
  the validator is bypassed.
- **L-3.** `ThrowOnBadRequest = true` applies to the whole REST host. Every minimal-API binding failure
  (unreadable body, unparsable route or query value) is therefore the `MALFORMED_REQUEST` problem
  document in every environment, not a bare `400`. The status is unchanged; gRPC is unaffected.
- **L-4.** The plate limit counts UTF-16 code units in .NET, while PostgreSQL's `varchar(16)` counts
  characters. The .NET check is therefore never looser than the column, so the domain refuses a
  too-long value before the database can.

### Decided (lead), rounds 2 to 4

- **L-5.** `StartMaintenance` checks `VEHICLE_ALREADY_UNDER_MAINTENANCE` first and
  `VEHICLE_HAS_MISSION_COMMITMENT` second. The order is unobservable — a committed vehicle cannot be
  under maintenance, because `CommitToMission` refuses a vehicle that is — so it is fixed only so the
  code and its tests cannot drift apart.
- **L-6.** Two round 1 tests were a census of that round's surface and became factually false when the
  module grew: `FleetHandlerShapeTests.The_slice_has_its_two_commands_and_one_query` and
  `VehicleEndpointMetadataTests.The_slice_maps_exactly_three_endpoints`. Both were extended to the new
  true inventory and renamed (`The_module_has_its_four_commands_and_two_queries`,
  `The_module_maps_exactly_six_endpoints`); every other assertion in those files is untouched, and the
  401/403 token matrix was not opened. Correcting a census the new slice makes false is in bounds;
  widening or weakening an assertion is not.
- **L-7.** Over gRPC, a `vehicle_id` that is not a UUID parses to `Guid.Empty`, which
  `GetVehicleValidator`'s `NotEmpty()` already refuses, so it answers `InvalidArgument` as a validation
  failure. No code is invented for an unparsable id. Measured over a real client channel, not inferred.
- **L-8.** The available list is ordered by `id`. The plan names no order; the id is a version 7 UUID,
  so the list and the cache entry built from it are stable between reads.
- **L-9.** The availability predicate lives in `Domain/VehicleAvailability.cs` as an
  `Expression<Func<Vehicle, bool>>`, which the read model passes to `.Where(...)` for translation to SQL
  and the unit tests evaluate in memory. Written inline in the adapter it would be testable only against
  PostgreSQL, which is unreachable here, and the commit rule would be forced to write it a second time.
  Still exactly one definition, which is what the plan requires.
- **L-10.** `VehicleCommittedToMission` and `VehicleReleasedFromMission` carry `(VehicleId, MissionId)`.
  The plan names the events but not their fields. They exist in rounds 2 to 4 only so the eviction
  handler covers the whole list the plan names; no aggregate method and no Contracts port raises them yet.
- **L-11.** The cached value is a concrete `AvailableVehicleView[]`, returned as `IReadOnlyList<...>`.
  An interface would make the round trip depend on how the adapter's serializer materializes a
  collection interface.
  **Closed question — recorded so the next reader does not reopen it.** The implementer observed that no
  test reads a value back out of Redis, because in a single process the in-process level always answers
  first, so the level-2 serialization round trip this decision exists to protect looked unexercised. The
  owner ruled that it **is** covered by `AvailableVehiclesCacheExpirationTests` once those run: an entry
  that has expired from the in-process level and is then read again is exactly that round trip.
  **No additional test is to be written.** The gap is *unrun*, like every other Redis-dependent test
  here — not *unwritten*.

- **L-35, amendment to decision 4 (owner, 2026-10-05).** An eviction during a Redis outage must not fail
  the event handler. `AvailableVehiclesCacheEvictionHandler` catches the failure from
  `ICache.RemoveAsync`, logs a warning carrying the cache key and the exception, and returns. The reason
  it was needed: `DefaultHybridCache.RemoveAsync` does not catch level-2 failures, unlike its read and
  write paths, so an eviction during an outage cleared the in-process level and then threw out of the
  handler, which Wolverine would retry or dead-letter. The in-process level is already cleared at that
  point and any level-2 entry dies by its 30-second TTL, so the outage degrades staleness — which F-6
  already bounds — rather than breaking event handling.
  The `try` wraps the one `RemoveAsync` call and nothing else; both `ThrowIfNull` guards stay outside it
  so a composition bug still surfaces; caller cancellation is excluded by an exception filter and
  propagates, while a level-2 client's own internal timeout is treated as the outage it is. The catch is
  broad because the failure's type arrives through the hybrid adapter and `StackExchange.Redis`, which
  this module may not reference — the guard is the `try`'s width, not the catch's.
  Proven by `CacheEvictionOutageTests` (16 cases), which **runs and passes here**, needing no Redis.
- **L-12.** `MPCoreCacheOptions.KeyPrefix` is applied by `HybridCacheAdapter` itself, so the module
  passes the bare key constant. Confirmed two ways: `MPCore.Caching.Hybrid` carries a member reference
  to `Qualify` and none to `KeyPrefix`, and with a configured prefix the adapter was observed turning
  the bare key into the prefixed one. Prepending it in the module would qualify twice and the eviction
  would miss the entry the read created.
- **L-13.** The client half of `vehicles.proto` is generated in the unit test project
  (`GrpcServices="Client"`, the file linked, never copied); the host item stays `GrpcServices="Server"`.
  The deployed host carries no client stub for a service it only serves, and the two projects cannot
  produce ambiguous types for one `.proto`.
- **L-14.** `GetAvailableVehicles` returns the list itself rather than `Result<T>`, as the plan's query
  table specifies, so `GET /api/fleet/vehicles/available` answers `Results.Ok(list)` and is the one
  Fleet endpoint that does not pass through the failure mapper. It has no failure case: an empty fleet
  is `200 []`, never `404`.
- **L-15, and a known limitation.** `GrpcServices` decides only which *service* stubs protoc emits; the
  *message* classes are emitted by every compilation of a `.proto`. Generating the client in the test
  project therefore produces a second copy of `Vehicle`, `VehicleType` and the rest in the same
  `csharp_namespace` the referenced host assembly already publishes, which is `CS0436` and, under
  `TreatWarningsAsErrors`, a build error. Accepted resolution: `<NoWarn>$(NoWarn);CS0436</NoWarn>` in
  `tests/Fserp.FleetOperations.UnitTests` only. `CS0436` means the source copy shadows the imported one,
  which is what the test wants — the client speaks its own generated messages to the server's, over the
  wire. `TreatWarningsAsErrors` stays on everywhere and no other diagnostic is silenced in any project.
  **The limitation**: this is a suppression, however narrow. The zero-suppression alternative is a small
  client-only project with no reference to the host, referenced with `<Aliases>FleetGrpcClient</Aliases>`
  and one `extern alias` line in the test. It was not done because it adds a project to the solution,
  which is beyond the authorized scope of this round. It is the first candidate for round 9, which adds
  the next gRPC service, or for round 11's sweep. **This correction came from `fleet-developer` against
  the lead's own review note, which had claimed the host's `GrpcServices="Server"` setting was what
  prevented the clash. It does not.**
