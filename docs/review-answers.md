# Technical review: the fifteen questions

Challenge section 22. Each answer is five lines or fewer and points at the file or test that proves it.

**On proof.** Most of this was built on a machine with no Docker and no reachable PostgreSQL or Redis.
**Docker became available at the end, and the whole suite has now run: 930 unit tests and 92 integration
tests, all passing, nothing skipped.** Both gates — the concurrency gate and the Redis cache gate — ran
and passed, so claims that stood as "designed, not yet run" for eleven rounds are now **measured**. The
compose stack was built and started for the first time too: three services, both dependencies healthy,
migrations applied by the compose-only gate, `/health/ready` answering `200`.

Where something is still unproven, this document says so rather than rounding up; question 15 lists
what is left. `docs/verification-runbook.md` is the procedure for reproducing any of it.

---

### 1. Why did you choose these Module Boundaries?

Four modules along *ownership of state*, not along nouns: Fleet owns whether a vehicle may be used,
Drivers owns whether a driver may be used, Operations owns the mission lifecycle and decides *when*
resources are committed, Administration reads the audit trail. The test is "who refuses?" — Fleet refuses
an unusable vehicle, Operations refuses an unassignable mission — and no two modules can refuse the same
thing. Administration owns no aggregate at all, which is why its `Domain/` folder is deliberately empty.
**Proof:** `docs/architecture.md` "Module map"; enforced by `ModuleBoundaryTests` in
`tests/.../Architecture/ArchitectureTests.cs`, which reads the `.csproj` files, because the compiler
drops an unused reference and a boundary that is only a convention is not a boundary.

### 2. What are your Aggregate Roots and why?

`Vehicle`, `Driver`, `Mission` — one per thing that is independently contended and independently
consistent. Each owns a concurrency token (`xmin`) because each can be changed by two actors at once.
`Qualification` is an entity inside `Driver`, not a root, because a qualification has no life of its own
and the duplicate rule must be checked against the driver's whole set under the driver's token.
Mission holds vehicle and driver **ids only** — never a Fleet or Drivers type — so a mission can never be
loaded with, or cascade into, another module's state.
**Proof:** `src/Modules/{Fleet,Drivers,Operations}/.../Domain/{Vehicle,Driver,Mission}.cs`.

### 3. Where is each Business Invariant enforced?

In the aggregate that owns the state, as a `BusinessRule` checked with `CheckRule(...)` **before any
mutation**, so a refusal leaves the aggregate untouched and raises no event. No property has a public
setter. Two invariants are additionally enforced by the database because they are set-based and no
aggregate can see the set: unique `plate_number`, and the two unique partial indexes that stop one
vehicle or driver holding two active missions.
Challenge preconditions 1-8 map to specific places: 1 in `Mission.Assign`, 2-5 in `Vehicle.CommitToMission`,
6-8 in `Driver.CommitToMission` — the table is in `docs/plans/operations.md`.
**Proof:** every `Domain/Rules/*.cs`; `FleetMessageCoverageTests` proves no rule can exist without a
message; the full illegal-transition matrix is driven from the enum in `tests/.../Operations/MissionTests.cs`.

### 4. How does Operations determine whether a Vehicle is available without breaking the Fleet module boundary?

It asks Fleet; it never reads Fleet's tables or its aggregate. `Fleet.Contracts.IVehicleAvailabilityReader`
returns a `VehicleSnapshot` **read from PostgreSQL, never from the cache**, because a caller may base a
decision on the answer. Operations references `Fleet.Contracts` and `Drivers.Contracts` and nothing else.
Crucially, the snapshot is *advisory*: Operations does not decide availability from it. The authoritative
check happens inside `Vehicle.CommitToMission`, against the row being written, under the vehicle's token.
**Proof:** `src/Modules/Fleet/.../Application/Contracts/VehicleAvailabilityReader.cs`;
`tests/.../Fleet/VehicleContractsPersistenceTests.cs` asserts the reader resolves and answers in a
container with **no cache registered at all** — measured: 9 tests, run against a real PostgreSQL, passing.

### 5. What happens when two requests attempt to assign the same Vehicle simultaneously?

Exactly one commits; the other is refused with `409`. Three independent mechanisms, deliberately
overlapping: `Vehicle.CommitToMission` refuses a vehicle already committed (`422`); the vehicle's `xmin`
token fails the losing `UPDATE` at commit; and `ux_missions_active_vehicle` refuses a second active
mission row even if a code path bypassed Fleet entirely. All three surface as
`fleetoperations/CONCURRENCY_CONFLICT`.
**Proof: measured, and it passed.** `tests/.../Operations/ConcurrentAssignmentTests.cs` — eight racers,
each with its own scope, context, connection and transaction, released by a `Barrier`; every outcome is
classified and the test fails on anything unclassified. **It has now run against a real PostgreSQL and
passed**, together with its six siblings (same driver, both resources contested, assignment racing a
maintenance start, and a raw-SQL bypass proving the partial index alone refuses the second row). This was
the single most important unproven claim in the repository for eleven rounds; it is no longer unproven.
`docs/verification-runbook.md` §2 reproduces it.

### 6. Where is your Transaction Boundary?

One command, one transaction, owned by the Wolverine middleware around the single `AppDbContext`. A
handler declares `IUnitOfWork` to run inside it and **never calls `SaveChangesAsync`**. `AssignMission` is
the demanding case: the mission row, the vehicle row, the driver row and the audit rows commit together
or not at all, which is why `IVehicleCommitments` and `IDriverCommitments` load, mutate and deliberately
**do not save**. The one intentional exception is a rejected-attempt audit row, written *detached* so it
survives the rollback that refused the operation.
**Proof:** `src/Modules/Operations/.../Application/Commands/AssignMission.cs`, steps 1-7 in the plan's
order; a unit test records the port-call sequence so a refactor cannot reorder it.

### 7. What exactly are you caching?

One entry: the whole result of `GetAvailableVehicles`, under the single key
`fleet:vehicles:available:v1`, 30 seconds absolute. Nothing else in the system is cached — not
`GetVehicle`, not the Contracts readers, not drivers, not missions, not the audit trail. The rule we held
to is that **nothing a transactional decision depends on is ever served from cache**; the list is
advisory and `AssignMission` re-checks every rule against the database.
**Proof:** `src/Modules/Fleet/.../Application/FleetCacheKeys.cs` (one constant, two call sites) and
`GetAvailableVehicles.cs`; the Drivers and Operations modules do not reference `MPCore.Caching` at all,
asserted on the assembly references.

### 8. How do you invalidate the Cache?

By domain event, not by guesswork. `AvailableVehiclesCacheEvictionHandler` has one `Handle` per event
that can change availability — `VehicleRegistered`, `VehicleStatusChanged`, `MaintenanceStarted`,
`MaintenanceCompleted`, `VehicleCommittedToMission`, `VehicleReleasedFromMission` — each removing the same
key. Removal is idempotent, so at-least-once delivery is harmless, and the 30-second TTL bounds any
missed eviction. Mission assign, complete and cancel reach the cache through the last two events.
**Proof:** a reflection test asserts the handler covers every domain event in the module, so a seventh
event cannot be added and silently forgotten; `tests/.../Fleet/CacheEvictionRoutingTests.cs` proves all six
are really *routed* by a live Wolverine runtime, not merely written.

### 9. What happens if Redis is unavailable?

Two different things, held to two different standards of proof. The distinction matters as much as the
answer.

**Reads degrade — measured, and it passed.** The endpoint still answers `200`, served from the in-process
level or from PostgreSQL, and the failure is logged; the cache is not a readiness dependency. The cost is
latency, since the Redis client's connect timeout is paid on the request.
*Proof:* `tests/.../Fleet/AvailableVehiclesRedisOutageTests.cs` induces a real outage with an in-process
TCP forwarder that resets live sockets, so it works against a container or a hosted Redis. **It has now
run against a real Redis and passed**, as have the six eviction cases and the two expiration cases — the
whole of architecture decision 4's cache gate. See `docs/verification-runbook.md` §3.3.

**Evictions no longer fail the handler — proven, by a test that runs here.** We found that
`DefaultHybridCache.RemoveAsync` does *not* catch level-2 failures the way its read path does, so an
eviction landing during an outage cleared the local level and then threw out of the Wolverine handler,
which would retry or dead-letter it. Decision 4 was silent on this; the owner amended it. The handler now
catches that failure, logs a warning carrying the cache key and the exception, and returns — correct
because the local level is already cleared and the Redis entry dies by its 30-second TTL, so the outage
degrades *staleness*, which decision 4 already bounds, rather than breaking event handling.
*Proof:* `CacheEvictionOutageTests`, 16 cases, **green on a machine with no Redis at all** — the one piece
of outage behaviour in this system that is demonstrated rather than designed. The catch is scoped so it
cannot hide bugs: the `try` wraps one call, both null-guards stay outside it, and caller cancellation
propagates untouched.

### 10. What is the difference between your Audit Trail and your Application Logs?

Different audiences, different guarantees. The **audit trail** is business fact: who did what to which
entity and whether it succeeded, written through `IBusinessAuditRecorder` and the persistence interceptor
into `audit.entries` **in the same transaction as the change**, queryable, retained, and masked by an
explicit allowlist policy — `FullName` is stored `Redact`ed. **Logs** are operational narrative:
OpenTelemetry, sampled, for diagnosing a request, and never a system of record.
The sharpest difference: a *rejected* operation produces an audit row and the transaction still rolls
back. Logs cannot do that; a rollback would take the evidence with it.
**Proof:** `src/Fserp.FleetOperations.Infrastructure/Audit/AuditPolicyConfiguration.cs` (default-deny
allowlist); `RecordAttemptAsync` in every refusal path; `docs/architecture.md` "Business audit path".

### 11. How do REST and gRPC reach the same Business Logic?

Both are thin adapters that build the **same command or query record** and send it through `IMessageBus`.
Neither contains a rule, a query or a mapping of its own — `VehicleService.GetVehicle` and the REST
`GET /vehicles/{id}` send an identical `GetVehicle` record. Failures are one model: a `FailureDescriptor`
rendered as Problem Details or as a gRPC status, with twin exception mappers so a unique violation and a
lost `xmin` race answer the same failure on both transports.
**Proof:** `src/Fserp.FleetOperations.Api/Grpc/Services/VehicleService.cs`; tests assert the gRPC service
sends the record REST sends, over a real client channel. **This is where we found a real bug**:
`?pageSize=0` returned a one-row page over REST while an absent gRPC `page_size` returned the default —
the transports normalised "not chosen" differently *below* the rule meant to prevent exactly this. Fixed
with one shared `Hosting/PageRequests.From`, pinned on both sides.

### 12. How would your design change if Messaging were introduced later?

Two things change; most does not. The in-process domain events already flow through Wolverine, so
handlers and the eviction path are unaffected. What must change is the pair of **writing** Contracts
interfaces: `IVehicleCommitments` and `IDriverCommitments` write to another module's aggregate inside the
caller's transaction, which is only defensible because this is one deployment sharing one `AppDbContext`.
With a broker they become module messages answered by Fleet and Drivers in their own transactions, and
`AssignMission` becomes a saga with compensation — losing the all-or-nothing guarantee decision 2 buys.
**Proof:** the reason is recorded *on the interfaces themselves*, with the revisit condition stated:
`src/Modules/Fleet/.../Contracts/IVehicleCommitments.cs`.

### 13. What did AI generate during development?

Effectively all of the code and tests, across eleven rounds, under a two-role split: a team lead that
briefed, reviewed and ran the build and tests, and a developer that wrote production code and tests. The
lead never wrote production or test code; the developer never updated the plan notes or `OUTCOMES.md`.
Every round the lead re-ran `dotnet build` and `dotnet test` itself rather than accepting reported
numbers — which caught a stale count (the developer reported 311 unit tests; the real figure was 317).
Humans supplied the decisions: the 33 numbered plan-note answers, the five architecture decisions, and
every scope authorization. AI was not permitted to invent a business rule, status, limit or workflow.
**Proof:** `docs/ai-development-notes.md`, round by round, with rejected suggestions and their reasons.

### 14. Which AI recommendation did you reject, and why?

Dozens, all recorded; the instructive ones are where AI was *plausibly* wrong.
**The sharpest:** the developer claimed the Wolverine unverified item could be struck because starting a
runtime compiles every handler chain. It does not — Wolverine builds a handler's executor lazily, on the
first message of that type. Rejected with the framework's own GH-4151 note as evidence, then settled by a
mutation with a negative control that distinguished `UnResolvableVariableException` from
`IndeterminateRoutesException`. The claim ran in the direction that flattered its author's work, which is
precisely when the evidence bar should rise.
**Others:** caching `GetAvailableDrivers` "by symmetry" (the plan says no); adding a
`DRIVER_NAME_TOO_LONG` rule nobody decided; a `pageSize` validator rule that could never fail;
prepending the cache key prefix the adapter already applies, which would have broken eviction silently.
**Three rejections went against the lead, not the developer** — a wrong claim about `GrpcServices`, a
missed `ValidateOnBuild` caveat, and an instruction to register a port that was already registered.
**Proof:** `docs/ai-development-notes.md`, "Suggestions rejected or corrected" in every round, with the
source column naming who was wrong.

### 15. If you had another week, what would you improve?

The biggest item on this list — "run the suite on a Docker-capable machine" — **has now been done**: 930
unit and 92 integration tests pass, both gates included, and the compose stack builds and starts. What
remains, in order:

1. **A CI pipeline.** The suite now genuinely proves the design, so the thing with the highest marginal
   value is running it on every push rather than once by hand. There is no pipeline and no git
   repository yet, by the owner's instruction.
2. **Review Git staging before the first commit.** A `.gitignore` has since been added for the local
   `.env` documented by Compose; verify that no real values or build output are staged.
3. **Add `changes` and `reason` to the audit view.** Today an `EntityChange` row says *that* something
   changed, not *what* — an investigator's first question. The data is already stored and already masked
   by policy; only the view omits it. It is a REST contract change, which is why it was not done
   unilaterally.
4. **Remove the one suppression**: `CS0436` in the unit test project (L-15), via a client-only project
   behind an `extern alias`. Narrow and documented, but still a suppression.
5. **Close the remaining honest gap in the cache gate**: `HybridCache.RemoveAsync` throwing during an
   outage is now handled and proven by a unit test, but nothing exercises an eviction landing mid-outage
   against a real Redis end to end.
6. **Start the host against a real OpenID Connect issuer.** Every `401`/`403` here is proven against an
   in-process test key — real policies and real claim mapping, but not a real issuer. Port separation is
   likewise only provable on real ports.
