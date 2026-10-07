# Bounded-context plan notes

Status: **approved by the owner** (five decisions, plan notes and answers to every open question).
The decisions are in [../architecture.md](../architecture.md#fleet-operations-design-decisions).

One note per module, written with `mpcore-plan-bounded-context`. Each note lists aggregates, invariants
(stable UPPER_SNAKE codes that become `BusinessRule` classes), commands, queries, domain events, the REST
and gRPC surface, authorization, audit points, cache candidates, dependencies and the decided answers to
its former open questions. Items marked **Decided (owner)** were answered by the owner; items marked
**Decided (default)** are the plan's proposed default, adopted by the owner's instruction.

| Module | Note | Owns | Depends on |
|---|---|---|---|
| Fleet | [fleet.md](fleet.md) | `Vehicle` (status, maintenance, mission commitment); cached available list; the `VehicleType` list | nothing |
| Drivers | [drivers.md](drivers.md) | `Driver` (status, qualifications, mission commitment) | `Fleet.Contracts` (`VehicleType` only) |
| Operations | [operations.md](operations.md) | `Mission` (lifecycle, assignment) | `Fleet.Contracts`, `Drivers.Contracts` |
| Administration | [administration.md](administration.md) | the audit read surface (no aggregate) | MP Core `IAuditQuery` only |

Fixed by `.mpcore/template-manifest.json`, not revisited here: `modular-monolith`, transport `both`,
messaging `none`, cache `hybrid`, business audit `postgresql`, MP Core `0.9.3`.

## Where these notes live

`ORCHESTRATION-PLAN.md` does not fix a path. The notes live here; each module folder
`src/Modules/<Context>/PLAN.md` links back to its note (one source, no copy).

## Conventions shared by all four notes

- **Error domain per module**: `fleet`, `drivers`, `operations`, `administration`. A rule's message key is
  `<domain>.<code in lower snake>`, e.g. `fleet.vehicle_under_maintenance`.
- **Failure mapping** (MP Core defaults, see architecture.md): validator failure `400` / `InvalidArgument`;
  broken `BusinessRule` `422` / `FailedPrecondition`; not found `404` / `NotFound`; lost optimistic
  concurrency or unique-index violation `409` (exact gRPC code confirmed against `MPCore.Transport.Grpc`
  in round 7); `401` and `403` from the host.
- **Route prefix per module**: `/api/fleet`, `/api/drivers`, `/api/operations`, `/api/administration`.
- **Identity** comes only from `ICurrentActorAccessor`. No command carries an actor id.
- **Time** comes only from `IClock`.
- **Capacity** is kilograms everywhere: a positive decimal, in Fleet (`Capacity`) and in Operations
  (`RequiredCapacity`); across a contract it travels as `decimal` named `...Kg`, in proto as a decimal
  string named `..._kg`.

## Cross-cutting decisions

- **X-1 Decided (default).** Keep the challenge path exactly, unversioned (`/api/fleet/vehicles/available`).
  Proto packages carry `v1`.
- **X-2 Decided (default).** Single-tenant. No tenant segment in cache keys, indexes or read ports.
- **X-3 Decided (default).** No `Idempotency-Key` in this scope; state transitions are guarded by state.
- **X-4 Decided (owner).** Capacity is kilograms, one positive decimal, the same unit for vehicle capacity
  and mission required capacity.
- **X-5 Decided (owner).** Closed list of vehicle types, defined once in `Fleet.Contracts` as
  `VehicleType { Van, Truck, HeavyTruck }`. Drivers references `Fleet.Contracts` for that list only.
