# Fserp.FleetOperations: outcomes and handover

Product `Fserp.FleetOperations`, generated from MP Core 0.9.3. Original Phase 1 handover:
2026-10-05. Latest recorded verification: 2026-10-07. **All eleven implementation rounds are complete.**
The Release build succeeded with 0 warnings and 0 errors; 943 unit and host tests passed on 2026-10-07.
The 92 integration tests passed with no skips on 2026-10-06, when the Docker image and Compose stack
were built and started and `/health/ready` returned 200. Sections 4b and 4c record these separate
verifications. Sections 1-4 retain
the dated Phase 1 and intermediate-round history; their then-open items are not the current status.

## 0. Scope note on this document

The handover request named a workflow with units U1 and U2, release candidates RC-6 and RC-2, an MVP-0
stage, a Construction phase and a CI pipeline. **None of those identifiers exist in this repository or
in its planning documents.** This project is tracked by the phases and rounds in
`../ORCHESTRATION-PLAN.md` and `docs/plans/README.md`. Where the request asked for an item that does not
exist here, this document says so explicitly rather than mapping it onto something else.

## 1. Phase 1 snapshot: completed and verified on 2026-10-05

Verified means checked on disk and by a build run on 2026-10-05, not taken from an agent's report.

### 1.1 Toolchain

| Item | Verified state |
|---|---|
| .NET SDK | 10.0.401 in a per-user SDK install (the account was not an administrator) |
| `mpcore` CLI | 0.9.3, global tool |
| `MPCore.Templates` | 0.9.3, template `mpcore-backend` installed |
| User PATH and `DOTNET_ROOT` | point at the user-level SDK root; terminals opened before the change still see the machine-wide 10.0.302 |

### 1.2 Generated backend (Phase 0)

Generated with: modular-monolith, transport both, messaging none, cache hybrid, business audit
PostgreSQL, AI tooling Claude. Recorded in `.mpcore/template-manifest.json`.

Wired by the generator and present in `Program.cs`: bearer-only OIDC resource server with deny-by-default
authorization, REST on 8080 and gRPC on 8081 with listener separation, OpenTelemetry logs, metrics and
traces, FluentValidation middleware, message catalog, hybrid cache registration, audit interceptor on
`AppDbContext`, Wolverine as transaction owner with handler discovery limited to the listed assemblies,
health probes, Problem Details, forwarded-header guard.

### 1.3 Architecture and plans (Phase 1, fleet-architect)

| Deliverable | Location | State |
|---|---|---|---|
| Five design decisions | `docs/architecture.md`, section "Fleet Operations design decisions" | accepted by owner |
| Module plan notes | `docs/plans/{README,fleet,drivers,operations,administration}.md` | approved; all 33 open questions marked decided |
| Module pointers | `src/Modules/<Context>/PLAN.md` | present |
| Six module projects | `src/Modules/Fleet`, `Fleet.Contracts`, `Drivers`, `Drivers.Contracts`, `Operations`, `Administration` | compile, registered |
| Contracts | `VehicleType` enum, `VehicleSnapshot`, `IVehicleAvailabilityReader`, `IVehicleCommitments`, `DriverSnapshot`, `IDriverEligibilityReader`, `IDriverCommitments` | present; the two writing ports carry their recorded reason |
| Proto contracts | `Protos/fleet/v1/vehicles.proto`, `Protos/operations/v1/missions.proto` | written; field numbers fixed |
| Registrations | `AddFleetModule`, `AddDriversModule`, `AddOperationsModule`, `AddAdministrationModule`; `HandlerAssemblies.cs`; `AppDbContext` configuration discovery; four resource files in the catalog | all four places, all four modules |

Module dependency direction, verified from the project files: Operations references only
`Fleet.Contracts` and `Drivers.Contracts`. Drivers references `Fleet.Contracts` for `VehicleType` only.
Fleet and Administration reference no other module. No module references the host.

### 1.4 Build result

```
dotnet build Fserp.FleetOperations.Backend.sln -c Release
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

Eight projects at that point, warnings treated as errors. The completed solution later added two test
projects; see section 4b for the current build and test counts.

### 1.5 The five accepted decisions, in one line each

1. Operations reads vehicle and driver snapshots through Contracts readers and commits both resources
   through Contracts commitment ports inside the Assign transaction; each aggregate enforces its own rules.
2. Optimistic concurrency via PostgreSQL `xmin` on Vehicle, Driver and Mission, plus unique partial indexes
   on active assignments per vehicle and per driver; one `AssignMission` command is one transaction.
3. Maintenance on a committed vehicle is refused with 422 and the refusal is audited; the operator frees
   the vehicle by cancelling the mission.
4. One cache key `fleet:vehicles:available:v1`, 30 second absolute expiration, evicted by a domain event
   handler on six Fleet events; nothing a command decides on is cached.
5. Policies `Operator`, `FleetManager`, `Administrator`, `OperationalReader`, role values from
   configuration, applied identically to REST and gRPC.

Owner decisions folded in: capacity in kilograms; closed vehicle type list Van, Truck, HeavyTruck; two
independent status fields on Vehicle; committed vehicles and drivers cannot be deactivated; Active on
registration; driver profile is full name only; FleetManager manages drivers; at least one qualification
at registration; scheduled time given at Schedule only; active missions are Scheduled, Assigned,
InProgress; OperationalReader covers Operators and Fleet Managers; Administrator reads audit only.

## 2. Phase 1 snapshot: environment-unverified at that time

At the Phase 1 handover, no code had run against PostgreSQL, Redis or an identity provider because no
runnable business behaviour existed. The table records what **was** planned then; section 4b records
the later PostgreSQL, Redis and Compose verification. A real OIDC issuer remains unverified.

| Verification | Requires | Planned in |
|---|---|---|---|
| Host starts and health probes answer | PostgreSQL, Redis, `Security:Authority`, `Security:Audiences` | round 1 |
| Migrations apply | PostgreSQL | round 1 and every schema change |
| Concurrent assignment commits exactly one | real PostgreSQL (`xmin` and partial indexes are PostgreSQL features) | round 7 gate |
| Cache eviction and Redis-down behaviour | Redis container, stopped mid-test | round 3 gate |
| 401 and 403 on REST and gRPC | tokens from a real OpenID Connect issuer, or a test issuer | every round with a policy |
| `docker compose up --build` succeeds | Docker | round 11 |

Docker was unavailable to the Phase 1 run. It was available for the later run in section 4b.

## 3. Phase 1 snapshot: open business and architecture decisions

**RC-6 and RC-2 do not exist in this project.** No decision carries those identifiers. The architecture
decisions are numbered 1 to 5 and are all accepted (section 1.5). The plan notes' questions are numbered
X-1 to X-5, F-1 to F-8, D-1 to D-6, O-1 to O-10, AD-1 to AD-3 and A-1, and all are marked decided.

Two corrections were given by the owner after Phase 1 and were **pending at that time**; the table is
retained as a historical record, not a current work queue:

| Item | Owner decision | Status |
|---|---|---|---|
| F-2 plate number | unique, enforced by a unique index mapped to 409; not masked in audit | `docs/plans/fleet.md` still says non-unique; to be updated by the implementer in round 1 |
| `Messaging:AutoProvision` key | remove, it is dead configuration | still present in `appsettings.json` |

One technical question is deliberately left to the implementer in round 7, recorded as O-9: which 409
code MP Core 0.9.3 produces for a concurrency exception and a unique violation, and whether a commit-time
loss can be audited as a rejected attempt.

## 4. Phase 2 implementation history (superseded by section 4b)

The dated entries below record the progression through Phase 2 and Phase 3 of
`../ORCHESTRATION-PLAN.md`. Their skipped tests and unverified gates describe those intermediate
runs, not the completed 2026-10-06 run in section 4b.

Rounds 2, 3 and 4 were run as **one authorized round** by `fleet-team-lead` with `fleet-developer`.
Lead's own run, not the developer's claim: `dotnet build -c Release` 0 warnings 0 errors; unit tests
**317 passed, 0 failed, 0 skipped** (201 before, so 116 new); integration tests **0 passed, 0 failed,
20 skipped** (11 of them new), every one skipped with `No PostgreSQL available: set
FLEETOPS_TEST_POSTGRES or make Docker available for Testcontainers (DockerUnavailableException: Docker
is either not running or misconfigured...)`. **No migration was required**: maintenance carries no data
(F-4) and `maintenance_status` already existed; `dotnet ef migrations has-pending-model-changes`
answered "No changes have been made to the model since the last migration." One defect was sent back
and fixed — a generated gRPC client stub had been added to the deployed host assembly to satisfy a
test — and the fix pass was re-verified. Decisions L-5 to L-14 are in `docs/plans/fleet.md`; the
round's notes, including twelve rejected or corrected suggestions, are in `docs/ai-development-notes.md`.

**Still unverified after this round, for want of an environment:** anything touching Redis (the round 3
gate in section 2 is NOT met — Docker absent, `localhost:6379` closed), anything touching PostgreSQL
(all 20 integration tests skip), and delivery of domain events after commit from the Wolverine durable
local queue. Routing of all six events to the cache eviction handler **is** proven, through a real
Wolverine runtime in `MediatorOnly` mode — but that proves only the six chains the test actually sends.
Chain compilation and port resolution of the four handlers it does not invoke (the two maintenance
commands and the two queries) stay unproven: Wolverine builds a handler's executor lazily, on the first
message of that type, per its own GH-4151 note, so a host that started is evidence of discovery, not of
resolution. The developer asked after closure for this item to be struck; the lead checked it against
the Wolverine package and kept it, because striking it would have made this report overclaim.

**One known limitation carried out of this round (L-15).** `tests/Fserp.FleetOperations.UnitTests`
suppresses `CS0436`, and nothing else, in that project alone: generating the gRPC client there emits a
second copy of the `.proto`'s message classes in the namespace the host assembly already publishes.
`TreatWarningsAsErrors` stays true everywhere and no other diagnostic is silenced in any project. The
zero-suppression alternative — a small client-only project referenced through an `extern alias` — adds
a project to the solution and was outside this round's authorized scope. It is the first candidate for
round 9 or for round 11's sweep.

| Round | Slice | Agent sequence | Status |
|---|---|---|---|
| 1 | Fleet: Register Vehicle, Change Status, policies, first migration | implementer, then verifier; closure by team lead with developer | **Accepted 2026-10-05** by fleet-team-lead: build 0 warnings 0 errors; unit 201 passed 0 failed; integration 10 skipped (no PostgreSQL), unverified. Owner findings 1-3 fixed, 6 recorded; see docs/ai-development-notes.md and docs/plans/fleet.md F-9 to F-12, L-1 to L-4 |
| 2 | Fleet: Start and Complete Maintenance | team lead with developer, one round | **Accepted 2026-10-05** by fleet-team-lead, as part of one round covering rounds 2 to 4 (note below) |
| 3 | Fleet: Get Available Vehicles with cache and eviction | team lead with developer, one round | **Accepted 2026-10-05**, but the Redis gate is **NOT met**: no Redis was reachable, so decision 4's cache behaviour is proven only at the handler level |
| 4 | Fleet: gRPC GetVehicle, GetAvailableVehicles, plus the gRPC twins of the host exception mappers (fleet.md F-10) | team lead with developer, one round | **Accepted 2026-10-05**; F-10 closed, REST/gRPC parity of `GetVehicle` measured over a real client channel |
| 5 | Drivers: Register, Change Status, Get Available Drivers, plus the Fleet and Drivers cross-module Contracts ports | team lead with developer (Batch A) | **Accepted 2026-10-05** by fleet-team-lead: build 0 warnings 0 errors; unit **520 passed, 0 failed, 0 skipped** (317 before, 203 new); integration **49 skipped** (20 before, 29 new), no Docker. Migration `20261005143953_AddDriversSchema`. Zero defects sent back. Decisions L-16 to L-21 in docs/plans/drivers.md. The rounds 2-4 Wolverine resolution question was **settled by experiment and struck** from the unverified list |
| 6-9 | Operations complete, as one batch: the six commands, the full state machine, `AssignMission` with both commitment ports in one transaction, the two reads, the two unique partial indexes, and gRPC `MissionService` | team lead with developer (Batch B) | **Accepted 2026-10-05** by fleet-team-lead: build 0 warnings 0 errors; unit **827 passed, 0 failed, 0 skipped** (520 before, so 307 new); integration **76 skipped** (49 before, so 27 new), no Docker. Migration `20261005152239_AddOperationsMissions` with both partial indexes. Zero defects sent back; the developer found and fixed two itself, one of them a real REST/gRPC paging-parity bug. **O-9 is answered** (L-26). Decisions L-22 to L-28 in docs/plans/operations.md. **Round 7's concurrency gate is NOT met**: the concurrent-assignment test is written as if it will run, and skips |
| 10-11 | Administration audit query behind `Administrator`; Dockerfile, docker-compose.yml, README, final sweep | team lead with developer (Batch C) | **Accepted 2026-10-05** by fleet-team-lead: build 0 warnings 0 errors; unit **914 passed, 0 failed, 0 skipped** (827 before, so 87 new); integration **83 skipped** (76 before, so 7 new). Zero defects sent back; the developer found and fixed one itself — a blank `Database__MigrateOnStartup=` crashed the host at boot instead of defaulting to off. One lead instruction was **wrong and withdrawn** (L-32). Decisions L-29 to L-34 in docs/plans/administration.md. **Every Docker artifact is unbuilt and unrun** |

## 4b. The verification debt — DISCHARGED on 2026-10-06

**Docker became available and the debt was paid.** Everything this section once listed as "written and
never executed" has now been executed. What follows is kept as the **reproduction procedure**, not as
outstanding work.

**The run, from the team lead's own invocation:**

```
dotnet build Fserp.FleetOperations.Backend.sln -c Release   ->  0 Warning(s)  0 Error(s)
dotnet test  Fserp.FleetOperations.Backend.sln -c Release --no-build
  Passed!  - Failed: 0, Passed: 930, Skipped: 0, Total: 930 - Fserp.FleetOperations.UnitTests.dll
  Passed!  - Failed: 0, Passed:  92, Skipped: 0, Total:  92 - Fserp.FleetOperations.IntegrationTests.dll
```

**Nothing skipped.** Both gates ran and passed: the **concurrency gate** (exactly one of eight racers
commits) and the **full Redis cache gate** (six eviction triggers, expiration, and a real mid-request
outage). The **compose stack was built and started for the first time**: three services, PostgreSQL and
Redis both reporting healthy with the app waiting on them, the compose-only migration gate firing
("Database:MigrateOnStartup is set: applying pending migrations before the host starts serving"), all
five schemas created — `fleet`, `drivers`, `operations`, `audit` and `wolverine`, the last confirming
Wolverine auto-provisions its own schema, which had been an open question — and `/health/ready`
answering `200 {"status":"UP"}`. Torn down with `docker compose down -v`.

**The first real run found four failing tests. All four were test bugs; no production defect.** Root
causes, each established against a live database *before* anything was changed:

1. `xmin` read as `Int64`. Npgsql maps CLR `uint` to `bigint`, so the reader was asked for the wrong
   type over an `xid` field. Now read as `text` and parsed back.
2. and 3. Two tests used lowercase `"fleet"` / `"operations"` where the **audit module name** is
   capitalised. They had conflated the *error domain* (lowercase, `fleet`) with the *audit module name*
   (capitalised, `Fleet`) — two different identifiers that happen to share a word. Both now use the
   module constants, so they cannot drift again.
4. `The_upper_bound_is_exclusive_and_the_lower_bound_is_inclusive` stepped by `AddTicks(1)`. PostgreSQL
   `timestamptz` resolves to a **microsecond**; a .NET tick is 100 ns, ten times finer, so the step
   truncated back onto the original stamp and the exclusive bound correctly excluded the row.
   **The production semantics were right; the test's technique was wrong.** It now steps a full
   microsecond and additionally proves two adjacent windows tile without overlap or loss.

Baseline commands, now the reproduction procedure:

```
cd <repo root>
dotnet build Fserp.FleetOperations.Backend.sln -c Release
dotnet test Fserp.FleetOperations.Backend.sln -c Release --no-build
```

No Docker but a reachable PostgreSQL? Point the fixture at a throwaway database instead:

```
FLEETOPS_TEST_POSTGRES="Host=...;Port=5432;Database=fleetops_test;Username=...;Password=..."
dotnet test tests/Fserp.FleetOperations.IntegrationTests -c Release
```

| # | Guarantee, and why it matters | Covered by | Command |
|---|---|---|---|
| 1 | **Exactly one of two racing assignments commits** — architecture decision 2 and the round 7 gate, now exercised against PostgreSQL. 8 racers, own context/connection/transaction each, `Barrier`-released; every outcome classified, assertion fails on anything unclassified. | `Operations/ConcurrentAssignmentTests.cs` (7 tests) | `dotnet test tests/Fserp.FleetOperations.IntegrationTests -c Release --filter FullyQualifiedName~ConcurrentAssignment` |
| 2 | **The two unique partial indexes actually refuse the second active mission**, including a raw-SQL bypass proving the index alone holds when Fleet is circumvented. | `Operations/ConcurrentAssignmentTests.cs` | as above |
| 3 | **`xmin` catches a lost update** on vehicle, driver and mission, and a commitment racing a maintenance start or a deactivation. | `Fleet/VehicleContractsPersistenceTests.cs`, `Drivers/DriverContractsPersistenceTests.cs`, `Operations/MissionPersistenceTests.cs` | `dotnet test tests/Fserp.FleetOperations.IntegrationTests -c Release` |
| 4 | **O-9's empirical half**: that PostgreSQL really raises `DbUpdateConcurrencyException` or a unique violation for two genuinely concurrent assignments, and that both become `409`. The static half is answered in `docs/plans/operations.md`. | `Operations/ConcurrentAssignmentTests.cs` | as #1 |
| 5 | **Every migration applies to an empty database**, and the three schemas (`fleet`, `drivers`, `operations`) match the model. | every fixture's `Database.MigrateAsync()`; `MaintenanceAndAvailabilityPersistenceTests` asserts no pending model changes | `dotnet test tests/Fserp.FleetOperations.IntegrationTests -c Release` |
| 6 | **The audit trail is really written and readable**: entity-change rows, business actions, and a rejected attempt surviving the rollback that refused it. | `Operations/MissionAuditTests.cs`, `Fleet/VehiclePersistenceTests.cs`, `Drivers/DriverPersistenceTests.cs` | `dotnet test tests/Fserp.FleetOperations.IntegrationTests -c Release --filter FullyQualifiedName~Audit` |
| 7 | **`FullName` reaches `audit.entries` Redacted** — the only personal data in the system. | `Drivers/DriverPersistenceTests.cs` | `--filter FullyQualifiedName~DriverPersistence` |
| 8 | **The audit query's provider behaviour**: newest first, `to` exclusive and `from` inclusive, each filter narrowing on its own column, paging without repeating or losing a row. A fake cannot establish it; the seven provider tests now have a recorded passing run. | `Administration/AuditTrailQueryTests.cs` (7 tests) | `--filter FullyQualifiedName~AuditTrailQuery` |
| 9 | **The availability predicates translate to SQL** rather than silently evaluating in memory, for vehicles and drivers. | `Fleet/MaintenanceAndAvailabilityPersistenceTests.cs`, `Drivers/DriverPersistenceTests.cs` | `dotnet test tests/Fserp.FleetOperations.IntegrationTests -c Release` |
| 10 | **Both Contracts commitment ports write nothing until the caller saves** — the guarantee the whole single-transaction assignment design rests on. | `Fleet/VehicleContractsPersistenceTests.cs`, `Drivers/DriverContractsPersistenceTests.cs` | `dotnet test tests/Fserp.FleetOperations.IntegrationTests -c Release` |

| 11 | **Round 3's cache gate, architecture decision 4**, in three parts: the available list is fresh after **each of the six eviction triggers**; an entry really expires at the configured value; and with **Redis cut mid-request** the endpoint still answers `200` from the in-process level or PostgreSQL, with the outage logged. Needs PostgreSQL **and** Redis; the skip reason names which is missing. | `Fleet/AvailableVehiclesCacheEvictionTests.cs` (6), `AvailableVehiclesCacheExpirationTests.cs` (2), `AvailableVehiclesRedisOutageTests.cs` (1) | `dotnet test tests/Fserp.FleetOperations.IntegrationTests -c Release --no-build --filter "Category=RedisCacheGate"` |

The outage is induced **without depending on Docker**: an in-process loopback TCP forwarder sits between
the cache client and the real Redis and resets every live socket when cut, so one test exercises a
Testcontainers Redis and a hosted one identically. `docs/verification-runbook.md` §3.3 states exactly
what a pass and a failure print. Redis is reached through `FLEETOPS_TEST_REDIS`, matching the
`FLEETOPS_TEST_POSTGRES` precedent.

### Historical verification debt and remaining limits

Being precise, because a command that silently proves nothing is worse than an admission:

1. ~~**Two gaps inside the Redis gate itself**~~ — **both closed by the owner on 2026-10-05. Neither is
   outstanding. Recorded so they are not reopened.**
   - `DefaultHybridCache.RemoveAsync` not catching level-2 failures was a real defect: an eviction during
     an outage cleared the in-process level and then threw out of the Wolverine eviction handler, which
     would retry or dead-letter it. **Decision 4 was amended** (`docs/architecture.md`, and
     `docs/plans/fleet.md` L-35): the handler catches that failure, logs a warning carrying the cache key
     and the exception, and returns — correct because the local level is already cleared and the entry
     dies by its 30-second TTL, so the outage degrades staleness rather than breaking event handling.
     **Proven by a test that runs on a machine with no Redis at all** — `CacheEvictionOutageTests`,
     16 cases, green. It is the only outage behaviour here that is demonstrated rather than designed.
   - **No test reads a value back out of Redis** — ruled **covered** by
     `AvailableVehiclesCacheExpirationTests`, which have now run and passed: an entry that has expired
     from the in-process level and is then read again *is* the level-2 round trip L-11 exists to
     protect. **No additional test is to be written.**
2. ~~**Post-commit domain-event delivery**~~ — **partly discharged.** The six eviction triggers now pass
   against a real PostgreSQL and Redis, which exercises the commit-then-evict path end to end. What
   remains strictly unproven is delivery through the *durable* local queue under a host restart or a
   failed handler, which no test stages.
3. ~~**Every Docker artifact**~~ — **discharged.** `docker compose up --build` built the image and started
   all three services. Now confirmed: the `sdk:10.0`/`aspnet:10.0` base tags, `$APP_UID` in the runtime
   image, `Grpc.Tools`' protoc inside the SDK image, both dependency health checks,
   `depends_on: condition: service_healthy`, `${VAR:-default}` interpolation, port publishing on 8080 and
   8081, the compose-only migration gate, and that **Wolverine auto-provisions its `wolverine` schema**
   on first start. The `--mount=type=secret` NuGet path was not exercised, because the default build does
   not need a private feed.
4. **The host has still never started against a real OpenID Connect issuer.** It has now started against
   a real PostgreSQL and Redis under compose, and answers `/health/ready`. But every `401`/`403` in this
   repository is proven against an in-process test key — real policies and real claim mapping, not a real
   issuer. **Port separation is also still unproven**: the ports are published and the app serves on
   both, but nothing asserts that a gRPC endpoint refuses a call arriving on the REST listener, because
   `Connection.LocalPort` is `0` under `TestServer` and no test drives the real ports.

**That list is now closed.** Everything the original handover said did not yet exist — aggregates, value
objects, business rules, domain events, commands, queries, validators, views, read ports, repositories,
EF mappings, migrations, REST endpoints, gRPC services, authorization policies, audit policy entries,
the test project and its tiers, `docs/ai-development-notes.md`, the Dockerfile, docker-compose.yml and a
project README — exists as of rounds 1 to 11.

Three items were outside the eleven implementation rounds; their later status is:

- **Persian resource texts:** added after the owner approved English and Persian project errors. All
  five project catalogs now have `.fa.resx` files and both transports negotiate `en`/`fa`.
- **A git repository:** initialized for the owner's publication workflow. `.gitignore` excludes local
  `.env` files, credentials and build output; staged files still need review before any commit.
- **A CI pipeline:** none exists. A Docker-capable runner could repeat the baseline command pair in
  section 4b on every push.

**CI pipeline status: none exists.**

## 4c. English/Persian project errors — verified on 2026-10-07

The owner approved Persian resource text after the eleven implementation rounds. All 31 project message
keys have English and Persian texts across Fleet, Drivers, Operations, Administration and Host. Both
MP Core failure adapters use English as the default and support Persian through `Accept-Language`.
Focused tests proved exact key and placeholder parity, nonblank translations, REST Problem Details and
gRPC rich `LocalizedMessage` rendering for `en`, `fa`, an unsupported language and no language header.
The gRPC `ErrorInfo` domain and code and the transport status remain stable.

```
dotnet build Fserp.FleetOperations.Backend.sln -c Release --no-restore
  Build succeeded. 0 Warning(s), 0 Error(s).
dotnet test tests/Fserp.FleetOperations.UnitTests -c Release --no-build --filter "FullyQualifiedName~ProjectMessageCultureTests|FullyQualifiedName~FailureLocalizationTests"
  Passed: 13, Failed: 0, Skipped: 0.
dotnet test tests/Fserp.FleetOperations.UnitTests -c Release --no-build
  Passed: 943, Failed: 0, Skipped: 0.
```

Docker Compose and the integration suite were not repeated for this resource/configuration-only change.
Their last recorded run is in section 4b. A real OIDC issuer, listener-level port separation, durable
queue recovery across restart, and the private-feed secret path remain unverified.

## 5. Known limitations and accepted risks

- Two Contracts interfaces write (`IVehicleCommitments`, `IDriverCommitments`). Accepted for one
  deployment with messaging none; must be redesigned if a module becomes a service.
- One active mission per vehicle and per driver at a time, because missions carry no end time.
- Up to 30 seconds of staleness in the available-vehicles list after a missed eviction or on a second
  host instance.
- No `Idempotency-Key` support in this scope; repeated transitions fail safely by state.
- Single-tenant; the host's tenant claim mapping is unused.
- The SDK is installed per user, not machine-wide. A different Windows account on this machine will not
  find it.

## 6. Reproduction commands

From the `backend/` directory, with the .NET 10 SDK available:

```
dotnet build Fserp.FleetOperations.Backend.sln -c Release
dotnet test Fserp.FleetOperations.Backend.sln -c Release --no-build
```

The integration project uses throwaway PostgreSQL and Redis Testcontainers when Docker is available.
If you supply `FLEETOPS_TEST_POSTGRES` or `FLEETOPS_TEST_REDIS`, use disposable services only; the tests
apply migrations and write data. Inspect the passed, failed and skipped counts instead of treating a
zero-exit test command as proof that integration tests ran. The 2026-10-06 run had 930 unit and host
tests and 92 integration tests passed, none failed or skipped.

For the local three-service stack, configure real local passwords and OIDC settings in an untracked
`.env` file, then run `docker compose up --build`. See the README for the required variables, host
configuration, migrations and REST/gRPC ports. The recorded Compose verification is in section 4b;
the stack does not include an identity provider.

## 7. Next recommended steps

Before the first commit, review `.gitignore` and the staged files for credentials or local build output.
Keep CI as a separate owner decision. Do not present real-OIDC interoperability, listener-level port separation, durable queue
recovery across a restart, or the private-feed BuildKit secret path as verified by the run above.
