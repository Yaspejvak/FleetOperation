# AI development notes

Maintained by `fleet-verifier`. One section per slice. Each entry records the tool, the task, what was
generated, what was changed by hand and why, and every suggestion that was rejected or corrected with
its reason. Entries come from the implementer's hand-off and from the verifier's own review; nothing is
recorded that neither source reported.

---

## Round 1: Fleet, Register Vehicle, Change Vehicle Status, Get Vehicle

Date: 2026-10-05. Plan: [plans/fleet.md](plans/fleet.md), shared conventions [plans/README.md](plans/README.md),
decisions in [architecture.md](architecture.md#fleet-operations-design-decisions).

### Tool and task

| | |
|---|---|
| Tool | Claude Code (agents `fleet-implementer`, then `fleet-verifier`), MP Core 0.9.3 skills `mpcore-implement-vertical-slice` and `mpcore-verify-business-behavior` |
| Task | Register Vehicle (`POST /api/fleet/vehicles`), Change Vehicle Status (`PUT /api/fleet/vehicles/{vehicleId}/status`), Get Vehicle (`GET /api/fleet/vehicles/{vehicleId:guid}`), the four policies, the first migration, the F-2 and AutoProvision corrections |

### What was generated (fleet-implementer)

- Fleet domain: `Vehicle` aggregate, `PlateNumber` and `Capacity` value objects, `OperationalStatus`,
  `MaintenanceStatus`, `VehicleDisplayStatus`, rules `VEHICLE_PLATE_NUMBER_REQUIRED`,
  `VEHICLE_CAPACITY_MUST_BE_POSITIVE`, `VEHICLE_HAS_MISSION_COMMITMENT`, events `VehicleRegistered`,
  `VehicleStatusChanged`.
- Fleet application: the two commands and one query with handlers and validators, `VehicleView`,
  `IVehicleRepository`, `IVehicleReadModel`, `FleetFailures` (`VEHICLE_NOT_FOUND`,
  `VEHICLE_PLATE_NUMBER_ALREADY_REGISTERED`), `FleetAudit`.
- Fleet infrastructure: EF mapping of `fleet.vehicles` (strings for enums, unique index
  `ux_vehicles_plate_number`, `xmin` token), repository and read model, `AddFleetModule<TContext>`.
- Host: `FleetOperationsPolicies` and `FleetOperationsPolicyContributor` (Operator, FleetManager,
  Administrator, OperationalReader), `AuthorizationRoleOptions`, `UniqueViolationExceptionMapper`,
  `VehicleEndpoints`, a host-wide `JsonStringEnumConverter(allowIntegerValues: false)`.
- Infrastructure: migration `20261005090650_InitialFleetVehicles` (schemas `audit` and `fleet`), the
  Fleet entry of `AuditPolicyConfiguration` (plate number not masked, F-2).
- Corrections from OUTCOMES.md section 3: `docs/plans/fleet.md` now says plate numbers are unique (F-2);
  `Messaging:AutoProvision` is gone from `appsettings.json`. Both confirmed on disk by the verifier.
- Tests: 101 unit tests, 10 PostgreSQL integration tests.

### Changed by hand

None reported to the verifier for this round.

### Items the implementer flagged in the hand-off

Recorded as the implementer stated them; the verifier's finding on each is in the next table.

| Flag | Verifier finding |
|---|---|
| A lost concurrency race returns 500, not 409; deferred to round 7 (O-9) | Confirmed 500 (test below). Deferral disputed: O-9 defers the code, not the status (finding 1) |
| No plate length limit | Confirmed; can surface as a 500 from the btree index (finding 2) |
| Rejected deactivation audited under action `VehicleStatusChanged`, outcome `Rejected` | Consistent with fleet.md, which names the outcome and failure, not a separate action. Accepted |
| Three role values under `Authorization:Roles` are required for the host to start | Confirmed (`ValidateOnStart` plus the contributor's check). Accepted: decision 5 defines all four policies host-wide |
| Domain events have no handlers yet | Confirmed. Accepted for round 1; delivery must be proved in round 3 |
| REST enums serialize as strings host-wide | Confirmed by host test. Accepted, but it changes every module's REST contract and should be recorded by the architect; it also produces finding 3 |
| `VEHICLE_NOT_FOUND` added for the 404 | Confirmed. Accepted; fleet.md's code table does not list it yet (finding 6) |

### Suggestions rejected or corrected

The implementer's hand-off reported no rejected suggestions. Corrections requested by the verifier's
review, each returned to the owner named:

| # | Correction requested | Reason | To |
|---|---|---|---|
| 1 | Map a lost optimistic-concurrency failure (`DbUpdateConcurrencyException`) to `409` | fleet.md "REST status codes" and plans/README.md "Failure mapping" decide `409` for lost concurrency; the host answers `500 mpcore.http/UNEXPECTED_FAILURE`. O-9 leaves only the exact code to round 7 | fleet-implementer (mapping); fleet-architect if the deferral is to stand |
| 2 | Decide a maximum plate length and enforce it in the validator and the column | An unbounded `text` value under a unique btree index fails in PostgreSQL above roughly 2.7 kB, which would be a `500`. The plan sets no limit, so the limit is the owner's decision, not the implementer's | fleet-architect, owner |
| 3 | Decide the REST contract for a body that cannot be read (integer or unknown enum value, empty body) | Today the answer is a bare `400` with no body and no content type, not a problem document with a field violation | fleet-architect |
| 6 | Add `VEHICLE_NOT_FOUND` to the code table in fleet.md | The code is part of the contract (404) and is absent from the plan | fleet-architect |

### What the verifier added

- `tests/Fserp.FleetOperations.UnitTests/Host/FleetOperationsHostFactory.cs`: the real `Program` in
  memory (`Microsoft.AspNetCore.Mvc.Testing` 10.0.12 from the local NuGet cache), bearer tokens signed by
  an in-process RSA key and validated by the real JwtBearer handler, hosted services removed so no
  database is contacted, `IMessageBus` replaced by a recording proxy.
- `Host/VehicleEndpointHostTests.cs`: authorization matrix for all three endpoints (401 without token,
  wrong key, wrong audience, wrong issuer, expired; 403 for a wrong role, no role, Administrator, a
  top-level `role` claim, identity headers; success for the right role) and the REST contract (201 and
  `Location`, 404, 409 from the handler and from the unique index at commit, 500 for an undeclared
  index, 422 with the rule code and text, 409 for lost concurrency, enum names in bodies, unreadable
  bodies never reaching the bus).
- `Architecture/ArchitectureTests.cs`: module references against the module map, no host or
  Infrastructure reference, no forbidden namespace in any module's `Domain` or `Application` folder,
  no forbidden type in Fleet signatures, every Fleet code and rule key has a text, handler shapes
  (queries have no unit of work, commands take only ports, no message carries an actor).
- `Fleet/Round1VerificationTests.cs`: derived status with maintenance, F-1 independence, refusal keeps
  the commitment, one event per real change, reactivation audit, no save on any path, no event on
  refusal, no attempt on the no-op, `VehicleRegistered` on the added aggregate, identity timestamp from
  `IClock`, the plate rule when the validator is bypassed, no attempt for a duplicate plate.
- Mutation check, reverted and confirmed byte-identical: removing the `AddHttpExceptionMapper` line in
  `Program.cs` failed only the new host test (the existing mapper unit tests do not go through the
  host); changing the GET policy to `FleetManager` failed the existing metadata test and two new tests.

### Verified in this environment

`dotnet build -c Release`: 0 warnings, 0 errors. `dotnet test -c Release`: unit tests 178 passed,
1 failed (`A_lost_concurrency_race_on_change_status_is_409`, finding 1), integration tests 10 skipped
(no Docker, no PostgreSQL, `FLEETOPS_TEST_POSTGRES` unset).

### Not verified in this environment

- Anything against PostgreSQL: the migration applies, the unique index refuses a duplicate, `xmin`
  refuses a lost update, entity-change audit rows, the rejected attempt surviving rollback. The ten
  integration tests exist and skip.
- The Wolverine middleware: validation `400` with field violations through the bus, commit and rollback
  by the middleware, whether a rule exception is retried (which would duplicate the rejected attempt),
  domain events stored and delivered after commit.
- A concurrent race of eight requests: no test sends parallel requests against a database.
- gRPC: no Fleet gRPC service exists until round 4.

---

## Round 1 closure: owner decisions on the verifier's findings 1, 2, 3 and 6

Date: 2026-10-05. Written by `fleet-team-lead` from its own review and from the `fleet-dev` hand-off.
The verifier's round 1 entry above is unchanged. Decisions are recorded in
[plans/fleet.md](plans/fleet.md) as F-9 to F-12 and L-1 to L-4.

### Tool and task

| | |
|---|---|
| Tool | Claude Code: agents `fleet-team-lead` (brief, review, gate) and `fleet-developer` as `fleet-dev` (code and tests); MP Core 0.9.3 skills `mpcore-implement-vertical-slice`, `mpcore-verify-business-behavior` |
| Task | Finding 1: lost concurrency is `409`. Finding 2: plate number at most 16 characters, in the value object and the column. Finding 3: unreadable body as a problem document. Finding 6 and the enum and gRPC records: plan-note entries by the lead |

### What was generated (fleet-dev)

- Host: `Hosting/ConcurrencyExceptionMapper.cs` and `Hosting/HostFailures.cs` (`fleetoperations/CONCURRENCY_CONFLICT`, category `Concurrency`); `Resources/HostMessages.cs` and `.resx`, registered in the message catalog; `ThrowOnBadRequest = true` in `Program.cs`.
- Fleet: rule `PlateNumberTooLongRule` (`VEHICLE_PLATE_NUMBER_TOO_LONG`) checked in `PlateNumber.Create`; `PlateNumber.MaxLength = 16`; the validator repeats the rule on the normalized value; `HasMaxLength(16)` on `plate_number`; the text in `FleetMessages.resx`.
- Migration `20261005120646_LimitVehiclePlateNumberLength`, generated by `dotnet ef` (`text` to `character varying(16)`, with a `Down`); model snapshot regenerated by the tool.
- Tests: 22 new unit tests. They cover the mapper, the host message text, the plate value object, the validator, the EF model and migration, a host 409 test with code and text, and a host 422 for the length rule; the unreadable-body test gains problem-document assertions. One integration assertion was added for the column type.

### Changed by hand

None. Each team lead edit was to documentation only.

### Suggestions rejected or corrected

| # | Suggestion | Outcome | Reason | Source |
|---|---|---|---|---|
| 1 | Report the concurrency failure under a Fleet code (e.g. `fleet/VEHICLE_CONCURRENCY_CONFLICT`) | Rejected | The mapper is host-wide and cannot tell which module's aggregate lost the race without reading entries; the red test throws with none. A host domain `fleetoperations` was chosen (L-1) | fleet-dev, confirmed by lead |
| 2 | Edit `InitialFleetVehicles` in place for the 16-character column | Rejected | A database that has already applied the initial migration would think it is current and keep `text`. EF migrations are append-only once shared; the cost is one `ALTER COLUMN` | fleet-dev |
| 3 | Build per-field violations (`VALIDATION_FAILED` with `vehicle_type`) from `JsonException.Path` | Rejected | Over the owner's twenty-line budget, and fragile parsing. MP Core already ships `MALFORMED_REQUEST`. Recorded as a limitation (F-12) | fleet-dev, confirmed by lead |
| 4 | A custom `IHttpExceptionMapper` for `BadHttpRequestException` | Rejected | Probed against the real host: MP Core's default mapper already answers it once the exception is thrown | fleet-dev |
| 5 | The lead's claim that an assembly `strings` scan found no MP Core concurrency code | Corrected | That scan timed out and was ASCII only; .NET string literals are UTF-16, so it could not see them. The developer's UTF-16 scan completed and is the evidence (L-1) | fleet-dev against lead |
| 6 | The lead's brief said the validator field path is `PlateNumber` | Corrected | The existing convention and MP Core's `ValidationFailures.ToViolation` report `plate_number`; the tests assert that | fleet-dev against lead |
| 7 | EF tooling warning "may result in the loss of data" on the `AlterColumn` | Accepted after review | The schema was never deployed, and PostgreSQL refuses an over-long value rather than truncating it | dotnet ef, reviewed by fleet-dev |

### Lead review

- One implementation pass and zero defects sent back. During the pass the lead sent two messages: a status request, and the L-1 decision on the concurrency code, sent while the developer was still searching MP Core for one.
- Diffed against a copy of the tree taken before the round: every change adds lines; the only replaced line is a stale test comment. No test file has fewer `Assert.` calls than before; the unchanged red test now passes.
- Mutation checks run by fleet-dev, and not repeated by the lead: removing the two host registrations failed exactly 5 host tests; removing the plate rule check failed exactly 3 tests; both files were restored byte-identical.

### Verified in this environment (lead's own run)

`dotnet build -c Release`: 0 warnings, 0 errors. `dotnet test -c Release`: unit tests 201 passed, 0 failed;
integration tests 10 skipped (no Docker, no PostgreSQL, `FLEETOPS_TEST_POSTGRES` unset).
`A_lost_concurrency_race_on_change_status_is_409` passes, also when run alone.

### Not verified in this environment

- The new migration applying, and `plate_number` being `character varying(16)` in PostgreSQL (the assertion exists in a skipped integration test).
- A real `xmin` race reaching `409` through the Wolverine middleware; the host test simulates it by throwing from the recording bus.
- Everything already listed as unverified in the verifier's round 1 entry.
- gRPC mapping of both exceptions: it does not exist until round 4 (F-10).

## Rounds 2 to 4 (one round): Start and Complete Maintenance, Get Available Vehicles with the hybrid cache, gRPC VehicleService

Date: 2026-10-05. Written by `fleet-team-lead` from its own review and from the `fleet-developer`
hand-off. Decisions are recorded in [plans/fleet.md](plans/fleet.md) as L-5 to L-14.

### Tool and task

| | |
|---|---|
| Tool | Claude Code: agents `fleet-team-lead` (brief, review, gate) and `fleet-developer` (code and tests); MP Core 0.9.3 skills `mpcore-implement-vertical-slice`, `mpcore-design-transport-contract`, `mpcore-apply-business-audit`, `mpcore-apply-security`, `mpcore-verify-business-behavior`, read from the canonical bodies under `.mpcore/skills/` |
| Task | The whole Fleet remainder in one authorized round: plan rounds 2 (maintenance), 3 (available list, cache, eviction) and 4 (gRPC reads plus the gRPC twins of the host exception mappers, F-10) |

### What was generated (fleet-developer)

- Domain: `StartMaintenance` / `CompleteMaintenance` on `Vehicle`, rules checked before any mutation; `VehicleAlreadyUnderMaintenanceRule`, `VehicleNotUnderMaintenanceRule`; events `MaintenanceStarted`, `MaintenanceCompleted`, `VehicleCommittedToMission`, `VehicleReleasedFromMission`; `VehicleAvailability` as the one availability expression.
- Application: the two maintenance commands with their validators and their audit calls; `GetAvailableVehicles` with `AvailableVehicleView`; `FleetCacheKeys` (one constant) and `FleetCacheOptions` (30 s, F-6); `AvailableVehiclesCacheEvictionHandler` with one `Handle` per event.
- Host: four REST routes added (two maintenance, the available list, keeping the `:guid` read); `Grpc/Services/VehicleService.cs`; `Hosting/GrpcExceptionMappers.cs` reusing `UniqueViolations.TryMap` and `ConcurrencyExceptionMapper.TryMap`.
- No migration: maintenance carries no data (F-4) and `maintenance_status` already existed. `dotnet ef migrations has-pending-model-changes` answered "No changes have been made to the model since the last migration."
- Tests: 116 new unit tests and 11 new integration tests.

### Changed by hand

None. Every team lead edit was to documentation only.

### Suggestions rejected or corrected

| # | Suggestion | Outcome | Reason | Source |
|---|---|---|---|---|
| 1 | Prepend `MPCoreCacheOptions.KeyPrefix` to the cache key inside the module, as the plan's wording "qualified by `KeyPrefix`" reads | Rejected | `HybridCacheAdapter` qualifies the key itself; doing it again yields `prefix:prefix:key`, so the eviction would miss the entry the read created — invisible today because this host configures no prefix, broken the day one is set (L-12) | fleet-developer, confirmed by lead |
| 2 | Cache `IReadOnlyList<AvailableVehicleView>` directly | Rejected | Makes the round trip depend on how the serializer materializes a collection interface; a concrete array is pinned by a round-trip test (L-11) | fleet-developer |
| 3 | A no-op path for `CompleteMaintenance` on a vehicle not under maintenance, by analogy with F-8 | Rejected | F-8 is scoped to `ChangeStatus`; fleet.md lists `VEHICLE_NOT_UNDER_MAINTENANCE` as a broken rule. Both maintenance refusals are hard | fleet-developer |
| 4 | A rejected-attempt audit for `CompleteMaintenance`, for symmetry with `StartMaintenance` | Rejected | fleet.md requires it only for the two `StartMaintenance` refusals. Its absence is asserted, not merely omitted | fleet-developer |
| 5 | An operational-status precondition on `StartMaintenance` | Rejected | F-4: an `Inactive` vehicle may start maintenance | fleet-developer |
| 6 | `GrpcServices="Both"` on the host's `vehicles.proto` item, so a test could open a real channel | Corrected | Put a generated client stub for a service the host only serves into the deployed assembly — a production project file changed to satisfy a test. Moved to the test project as `GrpcServices="Client"` with the file linked; the host item is back to `"Server"` (L-13) | lead against fleet-developer |
| 7 | `OperationalReader` on `POST .../maintenance/complete` | Corrected before any build | Decision 5 puts maintenance behind `FleetManager`; the endpoint metadata row now pins it | fleet-developer |
| 8 | The availability predicate written inline in the EF adapter | Corrected | Testable only against PostgreSQL, which is unreachable here, and the later commit rule would write it a second time. Moved to one Domain expression used by both (L-9) | fleet-developer |
| 9 | A test of the developer's that claimed to read IL and did not | Corrected | Replaced with a source-level check that actually proves "one definition only". A test that asserts less than its name claims is worse than no test | fleet-developer |
| 10 | Hand-declaring `Method<TReq,TResp>` with marshallers in the test instead of generating a client | Rejected | Hardcodes the package, service and method strings; a typo fails as `Unimplemented` and proves nothing | lead |
| 11 | An empty migration to "mark" round 2 | Rejected | The schema did not change and the tooling confirms it | fleet-developer |
| 12 | The developer's reported unit-test count of 311 | Corrected | The lead's own run of the same tree reported 317 passed. The lead's number is the one in the report; the developer's re-run agreed | lead against fleet-developer |
| 13 | The lead's review note claimed the host's `GrpcServices="Server"` setting is what prevents a duplicate-type clash with the test's generated client | Corrected | It does not. `GrpcServices` decides only which *service* stubs protoc emits; the *message* classes come out of every compilation of a `.proto`, so the messages exist twice in one `csharp_namespace` either way. That is `CS0436`, and under `TreatWarningsAsErrors` it failed the build on the first attempt. Resolved with `NoWarn` for `CS0436` in the test project only; the zero-suppression alternative is recorded as a limitation (L-15) | fleet-developer against lead |
| 14 | After the round closed, strike "Wolverine discovery and port resolution" from the unverified list, because starting a Wolverine runtime compiles the chain of every discovered handler and so would already have proved that the four command and query handlers resolve their ports | Rejected | Checked against Wolverine 6.31.0 rather than argued: its own documentation for `HandlerGraph.AssertPreBuiltTypesExist` (GH-4151) says a bad chain "was only discovered lazily, on the first message of that type, from inside `HandlerFor(Type)` while the executor was being built". The eager check exists only for `TypeLoadMode.Static`, which the fixture does not use. Startup proves discovery, not resolution. The correction would have made the owner's report overclaim, so the item stays on the unverified list | fleet-developer, rejected by lead |

### Lead review

- One implementation pass, one defect sent back (item 6 above), one fix pass, one re-review. No third pass was opened, so the `CS0436` suppression the fix required is recorded as a limitation (L-15) rather than reworked.
- The developer pushed back once, on a framework constraint, and was right; the lead's premise was wrong and is recorded as item 13 rather than quietly dropped.
- The lead ran `dotnet build -c Release` and `dotnet test --no-build` itself; the developer's counts were treated as a claim, not as evidence, and one of them was wrong.
- Checked by the lead file by file: every rule is a `BusinessRule` checked with `CheckRule` before any state change; no handler takes a `DbContext` or calls `SaveChanges`; the query handler declares no `IUnitOfWork` and no repository; REST and gRPC build the same records and send them through the bus; every REST endpoint and the gRPC service carry one of the four named policies and none is anonymous; the rejected attempt is recorded for both `StartMaintenance` refusals; the cache key is one constant with two call sites; no credential, realm URL or connection string was added.
- The three edits to existing tests were checked assertion by assertion against the L-6 allowance. The third, a `NotSupportedException` stub added to a private fake in `HandlerTests.cs`, was a compile necessity caused by the read port gaining a member; the developer flagged it rather than slipping it in, and no assertion changed.

### Lead decision L-15: the `CS0436` suppression in the unit test project — accepted, deliberately and with its scope named

Measured against the owner's non-negotiable, "do not weaken authorization, delete an assertion or relax
a default to make something pass." On its face a `NoWarn` that turns roughly twenty build errors back
into silence is relaxing a default to make something pass, so it is decided here explicitly rather than
left in a csproj.

**Accepted.** The reasons, in order of weight:

1. It silences one diagnostic, `CS0436`, in one project, `tests/Fserp.FleetOperations.UnitTests`.
   Verified against the files, not taken from the developer: `NoWarn` appears exactly once in the whole
   tree, in that csproj; `Directory.Build.props` still carries `TreatWarningsAsErrors=true` for every
   project and was not modified in this round; no production project file carries any warning setting.
2. It is not an assertion, an authorization rule or a security default. No test was deleted, no
   assertion relaxed, no policy widened. The suite grew by 116 tests in the same round.
3. `CS0436` is inherent to the thing being tested, not an accident. Generating both halves of one
   `.proto` necessarily produces the message classes twice in one `csharp_namespace`; the warning says
   the source copy shadows the imported one. A client speaking its own generated messages to the
   server's, over the wire, is precisely what the test exists to prove.
4. The reasoning is written into the csproj at the point of suppression, so the next reader does not
   have to reconstruct it.

**Rejected alternative, costed.** A small client-only project with no reference to the host, referenced
with `<Aliases>FleetGrpcClient</Aliases>` and one `extern alias FleetGrpcClient;` line in the test. Zero
suppression, no duplicate types visible to the compiler. Rejected for this round only because it adds a
project to the solution, which is a structural change beyond the authorized scope, and because the round
had already used both of its passes. It is the first candidate for round 9 or round 11.

**Carried to the owner as a known limitation**, not buried: see `OUTCOMES.md` section 4 and
`docs/plans/fleet.md` L-15.

### Verified in this environment (lead's own run)

`dotnet build -c Release`: Build succeeded, 0 Warning(s), 0 Error(s). `dotnet test --no-build`:
unit tests 317 passed, 0 failed, 0 skipped (201 before this round, so 116 new); integration tests
0 passed, 0 failed, 20 skipped. The developer's hand-off quoted a stale 311; the lead's 317 is the
figure of record and the developer's re-run agreed with it.

Proven without a database: every new rule and that a refusal changes nothing and raises no event; F-4
and F-1; the two new failure codes and their texts; the rejected-attempt audit on both `StartMaintenance`
refusals and its deliberate absence on `CompleteMaintenance`; the availability predicate including each
of the three ways a vehicle is unavailable; the query handler's key, expiration and factory-on-miss; the
eviction handler's coverage of all six events through a real Wolverine runtime in `MediatorOnly` mode;
that the `:guid` constraint does not shadow `/vehicles/available`; the policy on each new endpoint and a
403 case; REST/gRPC parity of `GetVehicle` and the observed category-to-status mapping over a real
client channel; both gRPC mappers answering the same descriptors as their HTTP twins.

### Not verified in this environment

- **Anything involving Redis.** Docker is absent and `localhost:6379` is closed. The hybrid cache is proven only at the handler level against a fake `IReadThroughCache`, plus the adapter's key qualification against a fake `HybridCache`. Decision 4's Redis-outage exercise — stop the container and confirm `GET /available` still answers `200` — is unrun. No test claims Redis-proven behaviour.
- **Anything involving PostgreSQL.** All 20 integration tests skip, including the 11 new ones: the availability predicate actually translating to SQL, the maintenance column write, the `MaintenanceStarted` entity-change row naming only `MaintenanceStatus`, and the rejected attempt surviving the rollback.
- **Delivery of domain events after commit** from the Wolverine durable local queue, which needs PostgreSQL. Routing to the eviction handler is proven; post-commit delivery is not.
- **RESOLVED IN ROUND 5 — the item below is struck.** The mutation check was run, it settled the question, and `Every_Fleet_command_and_query_chain_resolves_its_ports_and_runs` now exercises all four chains through a real runtime. The item is kept with its full reasoning because the disagreement it records is the useful part. See the round 5 entry for the evidence.
- ~~**Chain compilation and port resolution of the four handlers `CacheEvictionRoutingTests` does not invoke**~~ — `StartMaintenanceHandler`, `CompleteMaintenanceHandler`, `GetVehicleHandler` and `GetAvailableVehiclesHandler`, the last including its `IOptions<FleetCacheOptions>` parameter. The developer flagged this in the hand-off, then after the round asked for it to be struck on the grounds that starting a Wolverine runtime compiles every discovered chain. The lead checked and **rejected the removal**: Wolverine 6.31.0's own documentation for `HandlerGraph.AssertPreBuiltTypesExist` (GH-4151) states that a bad chain "was only discovered lazily, on the first message of that type, from inside `HandlerFor(Type)` while the executor was being built", and the eager alternative applies only to `TypeLoadMode.Static`, which that fixture does not use. Starting the host proves **discovery**; it does not prove resolution. Only the six event chains the `[Theory]` rows actually send are exercised. The other four resolve correctly as far as the registrations show, but that is an argument, not a run. The developer then checked the package independently, confirmed the quotation and the zero hits for `AssertWolverineConfigurationIsValid`, and withdrew the claim as wrong in its mechanism rather than merely unproven. One mutation check would settle it for good; it is carried to round 5, and its design is written down here rather than rediscovered:
  1. Drop one port that only the unexercised handlers need — `IVehicleRepository` — and see whether `InitializeAsync` throws.
  2. Pin the environment explicitly. `Host.CreateDefaultBuilder()` sets `ValidateOnBuild` and `ValidateScopes` from `IsDevelopment()`, so an unpinned run could fail the start for container reasons rather than Wolverine ones; assert on the exception's origin, not merely on the fact of a throw. (Developer's caveat. It is very likely not a factor — removing a descriptor removes the thing `ValidateOnBuild` would inspect, and a Wolverine handler is not a DI-registered service — but "very likely" is what this whole item is about.)
  3. **Include the negative control.** After a start that succeeds, send a `StartMaintenance` message and confirm *that* throws a resolution failure. Without it a clean start is ambiguous: it fits "resolution is lazy" and "the port was never needed at all" equally, and only the first is the question. (Lead's addition.)
  4. **Assert on which failure, not that one occurred.** If Wolverine excludes a chain whose dependency cannot be satisfied rather than building one that fails late, step 3 throws "no route for this message type" instead of a resolution error, and a loose `Assert.ThrowsAsync<Exception>` would read the first as the second and false-confirm. The two mean opposite things: a resolution failure says the chain existed and was built late, which is the lead's position; a missing route says the chain never existed and leaves the question open. Types present in `Wolverine.dll` 6.31.0 on the routing side, to be rejected explicitly: `IndeterminateRoutesException`, `NoHandlerForEndpointException`, `InvalidHandlerException`. The resolution side should surface a container `InvalidOperationException` naming `IVehicleRepository`, possibly wrapped by generated code, so assert on the type *and* on the port name appearing somewhere in the exception chain. Walk the chain rather than inspecting the outermost type: `UniqueViolations.TryMap` and `ConcurrencyExceptionMapper.TryMap` already do exactly that in this host, so there is a local precedent to follow instead of a convention to invent. Record the actual type and message in the test output either way, so a surprising third outcome is visible rather than swallowed. (Developer's hypothesis, lead's type names. Neither has established what Wolverine actually does here; that is to be read from the discovery types before the assertion is written, not inferred — which is the error this whole item already cost once.)
- **Port separation for the gRPC endpoint**: `RequireListenerPort` is applied, but `Connection.LocalPort` is 0 on `TestServer`, so the middleware short-circuits. Unchanged from round 1.

## Round 5 / Batch A: Drivers module, and the Fleet and Drivers cross-module Contracts ports

Date: 2026-10-05. Written by `fleet-team-lead` from its own review and the `fleet-developer` hand-off.
Decisions are recorded in [plans/drivers.md](plans/drivers.md) as L-16 to L-21.

### The binding constraint of this batch

**Docker is unavailable on this machine and always will be.** The owner's instruction was explicit:
implement exactly per decisions 1, 2 and 4, write the integration tests that would prove them, let them
skip, and report them unverified — a design trimmed to what runs here would be a defect. 29 new
integration tests were written; all 29 skip.

### Lead review

- One implementation pass, **zero defects sent back**, no fix pass needed. The lead ran the build and
  tests itself; the developer's counts were confirmed rather than taken on trust.
- Checked file by file: every D-coded rule and every Fleet commitment rule is a `BusinessRule` checked
  with `CheckRule` before any mutation; handlers take ports only and never `SaveChanges`; the two queries
  declare no `IUnitOfWork`; Drivers references only `Drivers.Contracts` and `Fleet.Contracts`, and does
  not reference `MPCore.Caching.Abstractions` at all; all four driver endpoints carry `FleetManager` or
  `OperationalReader` and none is anonymous; `FullName` is masked `Redact` in the audit policy; both
  commitment ports load through the repository and do **not** save; no credential or connection string
  was added.
- `VehicleEndpointMetadataTests` and `VehicleEndpointHostTests` were not opened. Drivers received its own
  endpoint census in a new file rather than extending Fleet's — the right call, because the new slice
  does not make Fleet's census false.

### The parked Wolverine experiment — run, and it settles the question

The lead's three candidate type names were checked against Wolverine 6.31.0 rather than assumed. With the
environment pinned to Production so `ValidateOnBuild`/`ValidateScopes` could not be the cause:

1. With `IVehicleRepository` removed from the container, the host **starts clean** — no exception.
2. Sending `StartMaintenance` on that host throws `JasperFx.CodeGeneration.UnResolvableVariableException`,
   naming `IVehicleRepository` and the generated `StartMaintenanceHandler...HandleAsync` method.
3. The discriminator was measured, not inferred: an unhandled message type gives
   `Wolverine.Runtime.Routing.IndeterminateRoutesException`, whose chain names no port, while the
   resolution failure names the port and contains none of the three routing types.
4. Control: with the port present, the same message runs the generated handler and answers normally.

**The lead's round 2-4 position is confirmed**: starting a Wolverine runtime proves **discovery only**;
chains are built lazily on the first message of their type. The unverified item is nonetheless **struck**
— not because startup proves resolution, but because `Every_Fleet_command_and_query_chain_resolves_its_ports_and_runs`
now sends all six Fleet messages through a real runtime, including the `IReadThroughCache` and
`IOptions<FleetCacheOptions>` parameters that were the original worry.

### Suggestions rejected or corrected

| # | Suggestion | Outcome | Reason | Source |
|---|---|---|---|---|
| 1 | A `DRIVER_NAME_TOO_LONG` business rule in `DriverName` | Rejected | The plan lists no length invariant; inventing one would be inventing a rule. The cap is the validator plus `varchar(128)` (L-17) | lead, in the brief |
| 2 | An `IReadThroughCache` on `GetAvailableDrivers`, by symmetry with Fleet | Rejected | D-6 and the query table say "Cached: no" for both. The module does not reference the caching package at all, and a test asserts that on the assembly's references | fleet-developer |
| 3 | An event handler for the four driver events | Rejected | The plan says there is none in this scope, and that an unrouted event dropped with a log "must not be counted as tested behaviour" | fleet-developer |
| 4 | A gRPC service for the driver reads, mirroring round 4 | Rejected | `drivers.md` records "gRPC: none" for both queries; adding one is a transport scope change | fleet-developer |
| 5 | Reusing Fleet's `OperationalStatus` enum for `Driver` | Rejected | Only `VehicleType` crosses from `Fleet.Contracts` (X-5). Two enums sharing two member names are not one vocabulary | fleet-developer |
| 6 | Making `IVehicleCommitments` save, so Operations cannot forget | Rejected | Decision 1 requires the caller's unit of work to commit; saving here splits one business transaction in two | fleet-developer |
| 7 | Reaching the committed state in new tests via the reflection setter `TestVehicles.CommittedTo` | Rejected | The commitment path is production code from this round on; new tests use `CommitToMission`. The helper is left for the existing tests | fleet-developer |
| 8 | Extending `VehicleEndpointMetadataTests` to cover drivers | Rejected | It is Fleet's census under Fleet's prefix, and the new slice does not make it false | fleet-developer |
| 9 | `Entity<TId>.CheckRule` used as an instance method in `Driver.Register` | Corrected | It is `protected static` (CS0176 on the first build). The fix is better than planned: both qualification rules are now checked before the aggregate is constructed, so a refusal builds nothing | fleet-developer |
| 10 | The experiment's control using `bus.InvokeAsync(message)` without a result type | Corrected | That treats the handler's `Result<T>` as a cascading message and fails in `MediatorOnly` for an unrelated reason — the control would have measured the wrong thing. Switched to `InvokeAsync<T>`, which is what the endpoints call | fleet-developer |

### Verified in this environment (lead's own run)

`dotnet build -c Release`: Build succeeded, 0 Warning(s), 0 Error(s). `dotnet test --no-build`: unit
tests **520 passed, 0 failed, 0 skipped** (317 before, so 203 new); integration tests **0 passed,
0 failed, 49 skipped** (20 before, so 29 new). Migration `20261005143953_AddDriversSchema` was generated
by `dotnet ef migrations add` against the design-time factory with no database contacted, and
`has-pending-model-changes` reports "No changes have been made to the model since the last migration."

### Not verified in this environment

- **All 49 integration tests skip**, with `No PostgreSQL available: set FLEETOPS_TEST_POSTGRES or make
  Docker available for Testcontainers (DockerUnavailableException: ...)`. The 29 new ones would have
  proven: the `drivers` schema and its two tables; the `varchar(128)` bound; the unique qualification
  index as the L-18 backstop; `DriverAvailability.Specification` translating to SQL; `xmin` on `drivers`
  catching a lost update and a commitment racing a deactivation; `FullName` arriving Redacted in
  `audit.entries`; qualification child rows carrying their vehicle type; a rejected `ChangeDriverStatus`
  surviving the rollback; both Contracts ports reading PostgreSQL and writing nothing until the caller
  saves; Fleet's commitment racing a maintenance start on `xmin`.
- **Anything involving Redis**, unchanged from rounds 2-4.
- **Delivery of domain events after commit** from the durable local queue, unchanged.

## Rounds 6 to 9 / Batch B: Operations complete

Date: 2026-10-05. Written by `fleet-team-lead` from its own review and the `fleet-developer` hand-off.
Decisions are in [plans/operations.md](plans/operations.md) as L-22 to L-28, with O-9 answered there.

### Lead review

- One implementation pass, **zero defects sent back**. The lead ran the build and tests itself.
- `AssignMission` was read line by line against the plan's seven steps, because the owner named the
  order a correctness property rather than a style preference. It matches: load mission (no audit on an
  unknown mission, L-22), both snapshot reads, `mission.Assign`, vehicle commitment, driver commitment
  with the vehicle type taken from step 2's snapshot, `RecordAsync`, middleware commit. Both refusal
  paths — `BusinessRuleValidationException` and the L-23 `ResultFailureException` — record a detached
  attempt and rethrow. A test records the sequence of port calls, so a later refactor cannot silently
  reorder steps 4 and 5 or hoist a commitment above the aggregate's own check.
- The state machine is one table read by every transition method, with `Completed` and `Cancelled`
  appearing as no edge's source and `InProgress` deliberately absent from `Cancel`'s sources (O-3).
  Every illegal edge is tested, driven from the enum, not only those a happy path would exercise.
- Checked: Operations' Domain holds no Fleet or Drivers type, only ids; `RequiredCapacity` is
  Operations' own value object rather than Fleet's `Capacity`; no cache port anywhere in the module;
  every endpoint and the gRPC service carry `Operator` or `OperationalReader`; the proto was not edited
  and the Api item stays `GrpcServices="Server"` with the client generated in the test project.

### Two defects the developer's own tests found, and fixed

1. **A real transport-parity bug.** `?pageSize=0` over REST produced a one-row page while an absent
   `page_size` over gRPC produced the default size: the two transports normalized "not chosen"
   differently. Fixed with one shared `Hosting/PageRequests.From`, pinned on both sides (L-28). This is
   the class of bug that "both transports send the same query record" exists to prevent, and it slipped
   in underneath that rule, at the binding layer.
2. A boundary test of the developer's own asserted that `VehicleType` appears in an Operations
   signature. It does not, and correctly so — it crosses only as a value inside `AssignMission`'s body.
   The wrong assertion was replaced with one pinning the true property, rather than relaxed.

### Suggestions rejected

| # | Suggestion | Outcome | Reason | Source |
|---|---|---|---|---|
| 1 | Reuse Fleet's `Capacity` for `RequiredCapacity` | Rejected | Puts a Fleet Domain type in Operations' Domain and reports a mission's own invariant under the `fleet` domain | fleet-developer |
| 2 | Two new Operations `409` codes for the partial-index violations | Rejected | Would add codes the plan's closed invariant table does not name, which is the owner's change, not the implementer's. Both map to the host-wide concurrency failure instead (L-26) | fleet-developer, confirmed by lead |
| 3 | Bound the `Location` column | Rejected | An invented limit; O-7 names only "non-empty" (L-27) | fleet-developer |
| 4 | Compensate the vehicle commitment when the driver commitment fails | Rejected | The middleware's rollback is the mechanism; a compensation would be a second write path for an event the transaction already undoes | fleet-developer |

### Verified in this environment (lead's own run)

`dotnet build -c Release`: Build succeeded, 0 Warning(s), 0 Error(s). `dotnet test --no-build`: unit
**827 passed, 0 failed, 0 skipped** (520 before, so 307 new); integration **0 passed, 0 failed, 76
skipped** (49 before, so 27 new). Migration `20261005152239_AddOperationsMissions` carries both partial
indexes with `filter: "status IN ('Assigned', 'InProgress')"`; `has-pending-model-changes` reports
"No changes have been made to the model since the last migration."

### Not verified in this environment

- **The concurrent-assignment test — the round 7 gate — is written and skips.** Eight racers, each with
  its own scope, context, connection and transaction, released together by a `Barrier`; every outcome
  classified as committed / refused by rule / lost `xmin` / refused by unique index, with an assertion
  that fails on anything unclassified. It asserts exactly one commit, the vehicle's
  `committed_mission_id` equal to the winner, `count(*) WHERE status IN ('Assigned','InProgress') = 1`,
  and every loser's mission still `Scheduled` with both ids null. **It has never run.** Nothing in the
  design was trimmed to what runs here, which is the owner's required outcome — but the gate is unmet
  and is reported as such.
- All 76 integration tests skip with `No PostgreSQL available: set FLEETOPS_TEST_POSTGRES or make Docker
  available for Testcontainers (DockerUnavailableException: ...)`.
- That PostgreSQL actually raises `DbUpdateConcurrencyException` or a unique violation for two genuinely
  concurrent assignments — the empirical half of O-9. The static half is answered in operations.md.
- **Anything involving Redis**, and post-commit event delivery, both unchanged.

## Rounds 10 and 11 / Batch C: Administration, and delivery

Date: 2026-10-05. Written by `fleet-team-lead` from its own review and the `fleet-developer` hand-off.
Decisions are in [plans/administration.md](plans/administration.md) as L-29 to L-34.

### Lead review

- One implementation pass, **zero defects sent back**. The lead ran the build and tests itself.
- Checked: the handler takes `IAuditQuery` and `CancellationToken` and nothing else; the `Domain/` folder
  stays empty; the module references no caching package; no view property is an `MPCore.Audit` type, so
  the REST contract does not move if the package type does; the endpoint carries `Administrator` and
  nothing else; reading the trail records no business action (AD-2), asserted across every method in the
  assembly rather than only the handler; AD-1 is tested in both directions — an Administrator token is
  refused on an `OperationalReader` endpoint, which is what "not a superset" actually means.
- Delivery: the migrate-on-startup gate defaults to **off** in `appsettings.json` and is turned on only
  by `docker-compose.yml`; two tests grep every `.json`/`.yml` in the tree to pin that. Every credential
  in the compose file is a `${VAR:-replace-me-*}` placeholder; the lead re-scanned the three new delivery
  files independently and found nothing real.

### A second defect found below the rule meant to prevent it

L-31 said a deployment that forgets the variable must not migrate. The gate was first written with
`GetValue<bool>(key, false)`, whose default applies only to an **absent** key: an **empty** one
(`Database__MigrateOnStartup=`, one stray keystroke in a `.env` or a k8s manifest) threw from the binder
and **crashed the host at boot**. Crashing is not "does not migrate". The developer's own test caught it,
not review. It now reads the raw value: blank or absent is `false`; a parseable boolean is honoured;
anything else (`yes`, `1`, `treu`) throws naming the key, because someone writing `yes` meant to open the
gate, and a silent "off" followed later by "relation does not exist" helps nobody.

### A lead instruction that was wrong, and was withdrawn

The brief told the developer to register the port and handler in `AddAdministrationModule`. The developer
pushed back with evidence: `IAuditQuery` is already registered by `AddMPCoreAudit<AppDbContext>` in
`Infrastructure`, and registering it in the module would force a reference to
`MPCore.Audit.EntityFrameworkCore.PostgreSql` — a persistence provider inside a module that owns no
persistence, which `ModuleBoundaryTests` exists to prevent. **Accepted; the brief was wrong** (L-32). The
developer proved both "verify" items against the real host container instead of asserting them in a
comment. This is the third correction recorded against the lead across the eleven rounds.

### Suggestions rejected or corrected

| # | Suggestion | Outcome | Reason | Source |
|---|---|---|---|---|
| 1 | A validator rule for `pageSize` | Rejected | `AuditPageRequest` normalizes itself and the provider caps again, so the rule could never fail for any caller input. A rule that cannot fail reads as protection and is not (L-34) | fleet-developer |
| 2 | A module-owned `IAuditEntryReadModel` with its own EF adapter | Rejected | L-29, and it would duplicate `EntityFrameworkAuditQuery` and let this module write SQL against `audit.entries`, which MP Core owns | fleet-developer |
| 3 | Caching the first page of the trail | Rejected | "The trail must be current when an administrator investigates". The module does not reference `MPCore.Caching` at all, asserted on the assembly's references | fleet-developer |
| 4 | Recording the read through `IBusinessAuditRecorder` | Rejected | AD-2 | fleet-developer |
| 5 | A gRPC `AuditService` | Rejected | AD-3 | fleet-developer |
| 6 | A scalar `subjectId` for "who" | Corrected | MP Core's `AuditActor.SystemActor` leaves `SubjectId` null and carries the name in `UserName`, so a scalar would answer "who" with `null` for every background change and every anonymous rejected attempt. A nested actor object instead (L-33) | fleet-developer |
| 7 | A compose-level `secrets:` block for the NuGet feed | Rejected after writing it | `file:` must exist or `docker compose up` fails outright, so the stack would not start out of the box. Replaced with a documented `docker build --secret` route | fleet-developer |
| 8 | A `curl`/`wget` health check on the app service | Rejected | Neither binary is in the ASP.NET runtime image, and adding one to obtain a health check that cannot be tested here is the "simplify toward what you could test" trap in reverse. The brief asks for health checks on the dependencies, which is what exists | fleet-developer |
| 9 | A separate one-shot `migrate` service in compose | Rejected | The env-var gate is what the brief specifies; the file notes that a one-shot job is the right shape for a real deployment | fleet-developer |
| 10 | The lead's instruction to register `IAuditQuery` in `AddAdministrationModule` | **Rejected, and the lead was wrong** | Already registered by `AddMPCoreAudit`; registering it again would pull a persistence provider into a module that owns no persistence (L-32) | fleet-developer against lead |

### The correction ledger across all eleven rounds

Kept because it is the honest record of how this work was produced. Corrections ran in both directions
throughout. **Three stand against the lead**: the `CS0436` mechanism and the Wolverine `ValidateOnBuild`
caveat (rounds 2-4), and the `AddAdministrationModule` registration (round 10). Roughly two dozen
developer suggestions were rejected or corrected across the batches, each recorded here with its reason
and its source. **Three defects were found by tests rather than by review**: the `pageSize=0` REST/gRPC
parity bug, the blank-env-var boot crash, and a boundary test that asserted something untrue. The one
disagreement neither side could settle by argument — whether starting a Wolverine runtime proves handler
resolution — was settled by a measurement with a negative control, and the person who ran it was the one
it proved wrong.

### Verified in this environment (lead's own run)

`dotnet build -c Release`: Build succeeded, 0 Warning(s), 0 Error(s). `dotnet test --no-build`: unit
**914 passed, 0 failed, 0 skipped** (827 before, so 87 new); integration **0 passed, 0 failed, 83
skipped** (76 before, so 7 new).

### Not verified in this environment

- **Every Docker artifact**: `docker build` and `docker compose up` never ran. Base image tags, `$APP_UID`,
  the BuildKit secret mount, both health checks, `depends_on: condition: service_healthy`, variable
  interpolation and port publishing are all unverified. The YAML parses and the folded connection strings
  resolve to single lines — checked by deserializing the file, which is the most that was possible here.
- **The audit query's provider behaviour** — newest first, `to` exclusive, each filter narrowing on its
  own column, paging without repeating or losing a row. Asserted in the README and in XML docs, measured
  nowhere; a fake cannot establish it.
- ~~**Round 3's cache gate is not merely unrun — no Redis fixture and no Redis-backed test exists**~~
  **— CLOSED by the closing brief below.** Nine tests now exist; they have still never executed, so the
  gate moved from *unwritten* to *unrun*. That is progress, not proof.
- All 83 integration tests, post-commit event delivery, and the host never having started against a real
  PostgreSQL, Redis or issuer — all unchanged.

## Closing brief: the Redis gate, the runbook, and the review answers

Date: 2026-10-05. Items 1 (the Redis gate tests) and 3 (`docs/verification-runbook.md`) ran first.
Item 2 (`docs/review-answers.md`) was **blocked** until the owner supplied the challenge document's
path, and was deliberately **not** reconstructed from `architecture.md` in the meantime: a plausible but
invented list of fifteen questions would have read as answered, which is worse than an absent file.

### What was added

Nine integration tests under the trait `Category=RedisCacheGate` — six eviction cases (one per trigger,
not collapsed), two expiration cases splitting the *value* from the *behaviour*, and one outage case.
Redis is reached through `FLEETOPS_TEST_REDIS`, matching the `FLEETOPS_TEST_POSTGRES` precedent; the
skip reason names which dependency was missing, so none can pass vacuously. Integration tests went
83 → 92 skipped; the 914 unit tests are unchanged.

**No production code changed.** The brief permitted a change only if a new test exposed a defect, and
none did — none of the nine can execute here. Only `tests/` and `docs/` moved.

### Two findings reached by reading the framework rather than running it

Both are now in `OUTCOMES.md` section 4b and `docs/verification-runbook.md` §7:

1. `DefaultHybridCache.RemoveAsync` does not catch level-2 failures, unlike its read and write paths. An
   eviction landing *during* a Redis outage clears the in-process level and then throws out of
   `ICache.RemoveAsync`, failing the Wolverine eviction handler, which retries or dead-letters.
   **Architecture decision 4 does not say what should happen there** — an open design question for the
   owner, not merely a missing test.
2. No test reads a value back out of Redis: in one process the in-process level always answers first, so
   the level-2 serialization round-trip of `AvailableVehicleView[]` is never exercised — exactly what
   L-11 exists to protect.

Both were offered by the developer as additional tests and **declined by the lead**. The brief
enumerated three behaviours; expanding scope at a closing brief is the lead's error to avoid, not the
developer's to absorb. They are recorded instead, which is the honest disposition.

### Suggestions rejected

| # | Suggestion | Outcome | Reason | Source |
|---|---|---|---|---|
| 1 | Add `RedisFixture` to the existing `PostgreSqlCollection` | Rejected | Would change the collection the 83 existing tests run in and start a Redis container for tests that need none. Cost accepted: with Docker, two PostgreSQL containers instead of one, serialized by `DisableParallelization` | fleet-developer |
| 2 | `container.StopAsync()` to induce the outage | Rejected | Docker-only, which would make the test unrunnable against a hosted Redis — defeating the escape hatch the brief required | fleet-developer |
| 3 | Shorten the Redis client timeouts in the outage test | Rejected | Decision 4 asks for the latency *including* the connect timeout to be documented; the test measures and reports it instead of hiding it | fleet-developer |
| 4 | `ConfigurationOptions.ToString()` to build the forwarder's connection string | Rejected | The default overload omits the password, breaking any hosted Redis that needs one. Token surgery preserves password, `ssl`, `abortConnect` and timeouts | fleet-developer |
| 5 | Hard-code `fleet:vehicles:available:v1` as the Redis key | Rejected | The test computes `MPCoreCacheOptions.Qualify(FleetCacheKeys.AvailableVehicles)`, so a key-prefix deployment cannot make the TTL assertion vacuous | fleet-developer |
| 6 | Promote the forwarder's parser checks into repository unit tests | Rejected | They would be non-skipped passing tests in a project where every test currently skips, and they are not part of the gate | fleet-developer |

### `docs/review-answers.md`

Challenge section 22's fifteen questions, five lines each, each pointing at the file or test that proves
it. Questions 5 and 9 — the concurrency gate and the Redis outage — were marked **"designed, not yet
run"**, because an unexecuted test must not read as demonstrated behaviour. (Both were updated to
**measured** after the run below.) Questions 13 to 15 are answered from this file's actual record: the
real rejected suggestions with their reasons, the three corrections standing against the lead, and the
real outstanding debt rather than an aspiration.

## The verification debt, discharged: the first run with Docker

Date: 2026-10-06. Docker became available. The whole suite ran for the first time.

### Result

Build 0 warnings 0 errors; unit **930 passed**; integration **92 passed, 0 failed, 0 skipped**. **Both
gates passed** — the concurrency gate (exactly one of eight racers commits) and the full Redis cache gate
(six eviction triggers, expiration, and a real mid-request outage). The compose stack built and started:
three services, both dependencies healthy, the compose-only migration gate firing, all five schemas
created including `wolverine` — confirming Wolverine auto-provisions its own — and `/health/ready`
answering `200 {"status":"UP"}`. Eleven rounds of "designed, not yet run" became measured.

### Four failures, all test bugs — and how that was established

The owner's brief named the trap explicitly: an empty page where rows are expected is as likely to be a
real defect in the audit query as a test bug, and the audit trail is a compliance surface, so a quietly
relaxed assertion would be the worst available outcome. **Nothing was changed until the cause was
proven.** The lead started a PostgreSQL container, pointed the fixture at it, re-ran the failing tests
and dumped `audit.entries` directly.

1. **`xmin` read as `Int64`.** Npgsql maps CLR `uint` to `bigint`, so the reader was asked for the wrong
   type over an `xid` field. Read as `text` and parsed back. Test bug.
2. **and 3. Lowercase module names.** The rows store `Fleet`, `Drivers`, `Operations` — capitalised,
   exactly as each plan note's "Audit module name" specifies. Two tests used `"fleet"` / `"operations"`:
   they had conflated the **error domain** (lowercase, carried by a `BusinessRule`) with the **audit
   module name** (capitalised, written to `audit.entries`) — two different identifiers that share a word.
   The clinching detail: in `A_rejected_attempt_is_readable_with_its_domain_and_code`, the line asserting
   the failure domain `"operations"` was **correct and passing**, while the line directly below it
   asserting the module `"operations"` was wrong. One test, both identifiers, one right and one wrong.
   Both now use the module constants so they cannot drift again.
4. **The empty page that was not where it looked.** The stack trace pointed at line 106, not line 97 — so
   the entry *had* been found and the exclusive upper bound *had* worked. What failed was the `included`
   query built with `stamp.AddTicks(1)`. PostgreSQL `timestamptz` resolves to a **microsecond**; a .NET
   tick is 100 ns, ten times finer. The step truncated back onto the original stamp, so `to` equalled the
   entry's timestamp and the exclusive bound correctly excluded it. **The production semantics were
   right; the test's technique was invalid against this database.** Fixed by stepping a full microsecond
   — and the test came out *stronger*: it now also proves two adjacent one-microsecond windows tile
   without overlap or loss.

**No production code changed in this phase.** The only production change of the whole closing sequence
was the separately-authorized decision 4 amendment, made before Docker arrived.

### Two things the developer flagged rather than quietly leaving

Both accepted and fixed in a second pass:

- `Each_filter_narrows_the_trail_on_its_own_column` passed **for two reasons and stated one**: a Fleet
  vehicle has no Operations entries, *and* the lowercase filter value matched nothing anyway. A test that
  is green for an unstated reason will mislead whoever later fixes the stated one. It now uses
  `OperationsAudit.Module`, so the emptiness is load-bearing.
- The class-level `<remarks>` on `AuditTrailQueryTests` still claimed the assertions "have therefore
  never been observed to pass", and that ordering and the exclusive bound were "MP Core's documented
  behaviour rather than this repository's measured behaviour". **This run falsified both.** Stale
  provenance claims on a compliance surface understate evidence that now exists and invite someone to
  re-litigate settled behaviour. Rewritten to say they are measured — scoped honestly to the database the
  run used, not to every provider or version.

The developer also swept both test projects for the same stale phrasing, found three further hits and
**left all three alone because each is still accurate** — they describe skip paths and handler-discovery
semantics, not test provenance. Correcting only what is now untrue is the right discipline; rewriting
accurate comments to look current would be the same error in the other direction.
