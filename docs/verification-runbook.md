# Verification runbook

**The debt this file was written to discharge has been discharged.** On 2026-10-06, with Docker
available, the whole suite ran: **930 unit tests and 92 integration tests, all passing, nothing
skipped.** Both gates passed, and the compose stack built and started for the first time.

This is therefore no longer a to-do list — it is the **reproduction procedure**. Use it to re-run the
proof on your own machine, in CI, or after a change. It assumes no knowledge of the project.
`OUTCOMES.md` section 4b records what the first real run found.

**What that first run found, so you know what "green" is worth here.** Four tests failed, and all four
were *test* bugs, not production defects: an `xid` column read as the wrong CLR type; two tests that
confused the lowercase *error domain* (`fleet`) with the capitalised *audit module name* (`Fleet`); and
one that stepped a timestamp by 100 ns when PostgreSQL `timestamptz` stores microseconds, so the step
truncated back onto the original value. Each was diagnosed against a live database *before* anything was
changed, and none was fixed by relaxing an assertion — the exclusive-upper-bound test came out stronger
than it went in.

Two gates matter more than the rest, and each has its own section below with **what a pass looks like**:

- **The concurrency gate** — exactly one of eight racing assignments commits (architecture decision 2).
- **The Redis outage gate** — the available-vehicles endpoint still answers when Redis dies mid-request
  (architecture decision 4).

---

## 0. What you need

One of these two environments. The second exists for machines that have no Docker either.

| | Option A — Docker | Option B — hosted services |
|---|---|---|
| PostgreSQL | started automatically (Testcontainers) | you supply, via `FLEETOPS_TEST_POSTGRES` |
| Redis | started automatically (Testcontainers) | you supply, via `FLEETOPS_TEST_REDIS` |
| .NET SDK | 10.0.x | 10.0.x |

Both environment variables are **connection strings to throwaway instances**. The tests create, migrate
and write to them. Do not point them at anything you care about.

```bash
# Option B only. Values are examples; use your own.
export FLEETOPS_TEST_POSTGRES="Host=localhost;Port=5432;Database=fleetops_test;Username=postgres;Password=postgres"
export FLEETOPS_TEST_REDIS="localhost:6379,abortConnect=false"
```

With Option A, set neither; the fixtures start containers themselves.

---

## 1. Baseline — build once, run everything

```bash
cd <repository root>          # the folder containing Fserp.FleetOperations.Backend.sln
dotnet build Fserp.FleetOperations.Backend.sln -c Release
dotnet test  Fserp.FleetOperations.Backend.sln -c Release --no-build
```

**The 2026-10-06 verified run printed:**

```
Build succeeded.  0 Warning(s)  0 Error(s)
Passed!  - Failed: 0, Passed: 930, Skipped: 0, Total: 930 - Fserp.FleetOperations.UnitTests.dll
Passed!  - Failed: 0, Passed:  92, Skipped: 0, Total:  92 - Fserp.FleetOperations.IntegrationTests.dll
```

**What you are looking for:** zero failed tests and zero skipped tests, including all integration
tests. Counts can change as tests are added; inspect the actual output from your run.

If integration tests still report `Skipped`, read the reason — it is printed in full and names exactly
what was missing. For example:

```
No PostgreSQL available: set FLEETOPS_TEST_POSTGRES or make Docker available for Testcontainers
(DockerUnavailableException: Docker is either not running or misconfigured...)
```

A skip is never a pass. If all integration tests skip on your machine, that run has not reproduced the
integration proof recorded on 2026-10-06.

---

## 2. The concurrency gate — reproduce the PostgreSQL proof

Architecture decision 2. Eight requests race to assign the same vehicle (and, in a sibling test, the same
driver). **Exactly one may commit.** The losers must be refused by the vehicle's `xmin` token, by one of
the two unique partial indexes, or by a business rule — and by nothing else.

```bash
dotnet test tests/Fserp.FleetOperations.IntegrationTests -c Release --no-build \
  --filter "FullyQualifiedName~ConcurrentAssignment"
```

Seven tests. Each racer runs in its own dependency-injection scope with its own `AppDbContext`,
connection and transaction, released together by a `Barrier` so the loads and snapshot reads genuinely
interleave.

### What a pass looks like

Test names and durations, nothing else. Behind each one, every racer's outcome has been classified into
exactly one of four buckets:

| Bucket | Meaning |
|---|---|
| **Committed** | the winner. There must be exactly **one**. |
| **Refused by rule** | `VEHICLE_NOT_AVAILABLE` and friends — Fleet's aggregate saw the vehicle already taken. |
| **Lost `xmin`** | `DbUpdateConcurrencyException` — the vehicle row changed under this transaction. |
| **Refused by unique index** | PostgreSQL `23505` on `ux_missions_active_vehicle` or `ux_missions_active_driver`. |

The assertions that must hold: exactly one commit; the vehicle's `committed_mission_id` equals the
winner's mission; `SELECT count(*) FROM operations.missions WHERE status IN ('Assigned','InProgress')`
returns `1`; and every loser's mission row is still `Scheduled` with both assigned ids `NULL`.

### What a failure looks like, and what it means

- **More than one "Committed"** — the serious one. Two missions hold the same vehicle. The `xmin` token,
  the partial index, or both are not doing their job. Do not ship.
- **`AssertEveryLoserIsAccountedFor` fails** — a racer failed in a way the test does not recognise. The
  message prints the unclassified exception. This usually means a new failure mode, not a false alarm.
- **Zero commits** — over-eager refusal; the design is safe but useless. Check that the `Barrier` is
  releasing and that the racers are not all refused by a stale precondition.

---

## 3. The Redis cache gate

Architecture decision 4. Nine tests, in three groups. These need **PostgreSQL and Redis together**; the
skip reason names which one is missing.

```bash
dotnet test tests/Fserp.FleetOperations.IntegrationTests -c Release --no-build \
  --filter "Category=RedisCacheGate"
```

### 3.1 Eviction — six tests

One per trigger: `VehicleRegistered`, `VehicleStatusChanged`, `MaintenanceStarted`,
`MaintenanceCompleted`, `VehicleCommittedToMission`, `VehicleReleasedFromMission`. After each, a read of
`GET /api/fleet/vehicles/available` must return the **fresh** list, not the pre-trigger one.

**Pass:** six green. **Failure:** the assertion prints the expected and actual vehicle identities — a
stale list means the eviction handler did not fire or removed a different key.

### 3.2 Expiration — two tests

One proves the **value**: the handler hands the cache the configured expiration and the module configures
30 seconds (F-6). One proves the **behaviour**: an entry really expires in Redis at the configured value
and the next read is fresh. The second composes a deliberately short expiration rather than sleeping 30
seconds, and computes the Redis key as `MPCoreCacheOptions.Qualify(FleetCacheKeys.AvailableVehicles)` so
a deployment with a key prefix cannot make the assertion vacuous.

### 3.3 The outage — one test

`The_available_list_still_answers_200_from_PostgreSQL_when_Redis_stops_mid_run_and_the_outage_is_logged`.

**How the outage is induced.** Not by stopping a container — that would only work under Option A and
would make the test unrunnable against a hosted Redis. Instead an **in-process loopback TCP forwarder**
sits between the cache client and the real Redis. The host's `ConnectionStrings:Redis` points at
`127.0.0.1:<ephemeral port>`; the forwarder relays bytes to the real Redis wherever it lives. Cutting the
forwarder stops the listener and closes every live socket with `Close(0)` — a **reset, not a graceful
close** — so the client's connection dies and every reconnect is refused by the loopback stack. This
behaves identically for a Testcontainers Redis and a hosted one.

The request under test is forced to be a genuine level-2 miss: the test removes the key through the
host's own `ICache` **while Redis is still up**, clearing both cache levels, and only then cuts. Without
that, the in-process level would answer and Redis would never be consulted.

**What a pass prints:** the test name and a duration of roughly the Redis client's connect timeout plus
the database read. The duration is expected to be seconds, not milliseconds — that latency *is* decision
4's documented cost of a Redis outage. Five things must hold: the warm-up returns `200` and the forwarder
counted at least one connection; no cache-failure log record exists before the cut; the request during
the outage returns `200`; its body identities equal what PostgreSQL holds; and at least one log record at
`Warning` or above names the cache failure. On the build machine the expected record was pre-measured as:

```
Error Microsoft.Extensions.Caching.Hybrid.HybridCache[6/CacheBackendReadFailure]
Cache backend read failure. -> StackExchange.Redis.RedisConnectionException
```

**What a failure prints:**

- Non-`200`: the status, the measured latency and the **entire response body**. The body is Problem
  Details and names the error domain and code. Decision 4 says that if the adapter throws instead of
  degrading, that is reported and not hidden — this is where you would see it.
- Missing log record: every captured record at `Warning` or above, one per line, as
  `Level Category[eventId/eventName] message -> ExceptionType: message`.
- Wrong list: expected versus actual vehicle identities.

---

## 4. Everything else the integration suite proves

Covered by the baseline run in section 1; listed so you know what you have just verified.

```bash
# Audit trail: entity-change rows, business actions, a rejected attempt surviving its rollback
dotnet test tests/Fserp.FleetOperations.IntegrationTests -c Release --no-build --filter "FullyQualifiedName~Audit"

# The audit read surface: newest first, `to` exclusive, each filter narrowing, paging without loss
dotnet test tests/Fserp.FleetOperations.IntegrationTests -c Release --no-build --filter "FullyQualifiedName~AuditTrailQuery"

# Drivers: schema, varchar(128), the unique qualification index, FullName stored Redacted
dotnet test tests/Fserp.FleetOperations.IntegrationTests -c Release --no-build --filter "FullyQualifiedName~Driver"

# Fleet: maintenance, the availability predicate translating to SQL, the Contracts ports
dotnet test tests/Fserp.FleetOperations.IntegrationTests -c Release --no-build --filter "FullyQualifiedName~Vehicle"
```

Two things worth watching for specifically, because they are claims rather than mechanics:

- **`FullName` must arrive in `audit.entries` Redacted.** It is the only personal data in the system.
- **Both availability predicates must translate to SQL**, not evaluate in memory. A silent client-side
  evaluation would still pass a naive assertion; these tests check the generated query.

---

## 5. Migrations

The integration fixtures migrate automatically. To apply them by hand against a real database:

```bash
cd src/Fserp.FleetOperations.Api
ConnectionStrings__PostgreSql="<your connection string>" dotnet ef database update
```

To confirm the model and the migrations still agree — this should print
`No changes have been made to the model since the last migration.`:

```bash
cd src/Fserp.FleetOperations.Api
dotnet ef migrations has-pending-model-changes
```

Three migrations exist: `InitialFleetVehicles`, `LimitVehiclePlateNumberLength`, `AddDriversSchema`,
`AddOperationsMissions`. They create schemas `fleet`, `drivers` and `operations`; MP Core owns `audit`
and Wolverine owns `wolverine`, both provisioned by their own packages.

---

## 6. Docker — built and started, 2026-10-06

```bash
docker compose config          # resolves variables and validates — do this first
docker compose up --build
# ... then
docker compose down -v
```

**Observed on the first real run:** the image built; all three services started; PostgreSQL and Redis
both reported **healthy** and the app waited on them via `depends_on: condition: service_healthy`; the
compose-only migration gate fired, logging *"Database:MigrateOnStartup is set: applying pending
migrations before the host starts serving"*; all five schemas were created — `fleet`, `drivers`,
`operations`, `audit` and `wolverine`, the last confirming **Wolverine auto-provisions its own schema**,
which had been an open question; ports 8080 and 8081 published; and `/health/ready` answered
`200 {"status":"UP"}`.

So these are now confirmed, not assumed: the `sdk:10.0` / `aspnet:10.0` base tags, `$APP_UID` in the
.NET 10 runtime image, `Grpc.Tools`' protoc inside the SDK image, both health checks,
`${VAR:-default}` interpolation, and port publishing.

**Still not exercised:** the `--mount=type=secret` NuGet-feed path, because the default build needs no
private feed.

**Before you run it:** every credential in `docker-compose.yml` is a placeholder containing `replace-me`.
Override them with a local `.env`. The repository's `.gitignore` excludes `.env` and `.env.*`; verify
that no real values are staged before committing.

The compose stack is also the only place migrations are applied on startup, via
`Database__MigrateOnStartup: "true"`. The default everywhere else is **off**: a deployment that forgets
the variable does not migrate, and does not silently migrate either.

---

## 7. What is still not proven, even after the real run

Updated 2026-10-06. Everything else in this file has now been executed and passed.

- **The host has never started against a real OpenID Connect issuer.** Every `401` and `403` result comes
  from the in-memory host with tokens signed by an in-process test key. The policies and the claim
  mapping are real; the issuer is not. Point `Security:Authority` at a real realm and re-run the host
  tests against it to close this.
- **Port separation is unproven end to end.** `RequireListenerPort` is applied to every endpoint, but
  `Connection.LocalPort` is `0` under `TestServer`, so the middleware short-circuits in tests. Only a
  running host on real ports 8080 and 8081 proves it.
- **Durable post-commit event delivery.** The six eviction triggers now pass against a real PostgreSQL
  and Redis, so the commit-then-evict path *is* proven end to end. What remains unproven is delivery
  through the Wolverine **durable** local queue across a host restart or a failed handler — no test
  stages that.
- **An eviction landing mid-outage, end to end.** `DefaultHybridCache.RemoveAsync` not catching level-2
  failures was a real defect; decision 4 was amended and the handler now catches it, logs the key and the
  exception, and returns. That behaviour **is** proven, by `CacheEvictionOutageTests` in the unit suite.
  What is not staged anywhere is an eviction arriving while a real Redis is down, as opposed to a fake
  that throws.
- The `--mount=type=secret` NuGet-feed path in the Dockerfile (section 6).

**Closed since this file was first written**, recorded so nobody reopens them: the level-2 serialization
round trip of `AvailableVehicleView[]` is covered by the expiration tests, which have now run and passed
— an entry that expires from the in-process level and is then read again *is* that round trip.
