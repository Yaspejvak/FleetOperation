# Fserp.FleetOperations

A fleet operations backend: vehicles, drivers, missions, and a readable business audit trail.
Generated from MP Core `0.9.3` and built on it; the framework arrives as NuGet packages and its
source is never copied here.

| | |
|---|---|
| Shape | `modular-monolith` |
| Transport | `both` — REST on `:8080`, gRPC on `:8081` |
| Messaging | `none` |
| Business audit | `postgresql` |
| Cache | `hybrid` — in-process first, Redis second |
| MP Core | `0.9.3` |

`.mpcore/template-manifest.json` is the source of truth for that table. Those are decisions already
made, not defaults to revisit.

## What is here

Four modules under `src/Modules`, one bounded context each, sharing one `AppDbContext` and one schema
per module.

| Module | Owns | Surface |
|---|---|---|
| Fleet | `Vehicle`: status, maintenance, mission commitment; the cached available list | REST + gRPC reads |
| Drivers | `Driver`: status, qualifications, mission commitment | REST only |
| Operations | `Mission`: lifecycle and assignment | REST commands, REST + gRPC reads |
| Administration | nothing — it only reads the audit trail | REST only |

The design decisions, with the alternatives that were rejected and why, are in
[docs/architecture.md](docs/architecture.md); the per-module detail is in [docs/plans/](docs/plans/).

### Verification status

The 2026-10-07 Release build completed with 0 warnings and 0 errors, and all **943 unit and host tests**
passed with 0 failed and 0 skipped, including English/Persian REST and gRPC failure tests. On
2026-10-06, before localization, **92 integration tests** passed against real PostgreSQL and Redis
with no skips. The Docker image and three-service Compose stack were built and started; PostgreSQL
and Redis became healthy, migrations ran, and `/health/ready` returned `200`. Docker and integration
tests were not repeated for the localization-only change. See [OUTCOMES.md](OUTCOMES.md) sections 4b
and 4c for the recorded runs and reproduction commands.

The host has **not** been tested against a real OIDC issuer: authentication and authorization tests use
an in-process signing key. Listener-level REST/gRPC port separation and durable local-queue delivery
across a restart or failed handler remain unverified. The private-feed BuildKit secret path has not been
exercised because the default image build did not need it.

## Run

### Locally

Prerequisites: the .NET SDK for `net10.0`, access to the NuGet feed serving the `MPCore.*` packages, a
PostgreSQL, a Redis, and an OIDC issuer if you intend to call anything but the health probes.

```bash
dotnet restore
dotnet build -c Release
```

Supply the settings the host requires (see [Configuration](#configuration)) and run it:

```bash
dotnet run --project src/Fserp.FleetOperations.Api
```

The host does **not** create or migrate its schema on this path. Apply migrations first — see
[Migrations](#migrations).

```bash
curl -i http://localhost:8080/health/live
```

The health probes are the only anonymous endpoints. Everything else answers `401` without a valid
bearer token; that is correct behaviour, not a misconfiguration.

### With Docker Compose

```bash
docker compose up --build
```

Three services: the application, PostgreSQL 17 and Redis 7. The database and the cache carry health
checks, and the application waits for both to report healthy before it starts, so the migration it runs
cannot hit a socket that is listening but not ready.

`docker-compose.yml` ships **placeholders only**: every credential defaults to a value containing
`replace-me`, and nothing in it is a working secret. Override them from a local `.env` file that you
never commit, or from your deployment's secret store:

```bash
# .env — never commit this file
POSTGRES_USER=fleetops
POSTGRES_PASSWORD=<your local password>
REDIS_PASSWORD=<your local password>
SECURITY_AUTHORITY=https://<your-issuer>/realms/<your-realm>
SECURITY_AUDIENCE=<the audience this API accepts>
ROLE_OPERATOR=<the operator role name in your tokens>
ROLE_FLEET_MANAGER=<the fleet manager role name>
ROLE_ADMINISTRATOR=<the administrator role name>
```

The stack starts without a `.env`, with the placeholder values, which is enough to exercise the health
probes and to see the host refuse everything else. It cannot serve a protected endpoint until
`SECURITY_AUTHORITY`, `SECURITY_AUDIENCE` and the three role names name something real — the issuer is
not part of this stack and never will be, because this host is a bearer-only resource server and never
hosts login.

If your NuGet feed is not reachable anonymously, the build stage needs its configuration. Pass it as a
BuildKit secret, which is mounted for one `RUN` and never lands in a layer, and let compose use the
resulting tag:

```bash
docker build --secret id=nuget_config,src="$HOME/.nuget/NuGet/NuGet.Config" -t fserp-fleetoperations:local .
docker compose up        # no --build: the tag already exists
```

**Migrations in compose.** The application service sets `Database__MigrateOnStartup=true`, and it is the
only thing in this repository that does. `src/Fserp.FleetOperations.Api/Hosting/DatabaseStartup.cs`
holds the gate; `appsettings.json` ships `Database:MigrateOnStartup` as `false`, so **every other path
— `dotnet run`, `dotnet test`, any deployment that does not set the variable — does not migrate, and
does not silently migrate either.** A deployment that forgets the variable starts without touching the
schema. Running migrations from the application process is a convenience for a throwaway stack, not a
recommendation: several instances starting at once would race on the same schema, and the runtime role
would need DDL rights it should not keep.

The image runs as the non-root `$APP_UID` user from the official ASP.NET base image, exposes `8080` and
`8081`, and bakes in no secret and no connection string.

## Configuration

Nothing secret belongs in a tracked file. Use **user secrets** while developing and **environment
variables** everywhere else. Never a command-line argument: it ends up in shell history and in process
listings.

```bash
dotnet user-secrets init --project src/Fserp.FleetOperations.Api
dotnet user-secrets set "ConnectionStrings:PostgreSql" "<your connection string>" --project src/Fserp.FleetOperations.Api
```

Every configuration key has an environment-variable form in which `:` becomes `__`, for example
`ConnectionStrings__PostgreSql`.

### Required — the host does not work without these

| Setting | Environment variable | What it is |
|---|---|---|
| `Security:Authority` | `Security__Authority` | your OIDC issuer. `appsettings.json` ships `https://identity.invalid/realms/replace-me` |
| `Security:Audiences:0` | `Security__Audiences__0` | the audience this API accepts |
| `ConnectionStrings:PostgreSql` | `ConnectionStrings__PostgreSql` | ships `replace-me` credentials on purpose |
| `ConnectionStrings:Redis` | `ConnectionStrings__Redis` | StackExchange.Redis configuration. Keep `abortConnect=false` so a Redis outage degrades the cache rather than the host start |
| `Authorization:Roles:Operator` | `Authorization__Roles__Operator` | the role name your issuer puts in a token for an operator |
| `Authorization:Roles:FleetManager` | `Authorization__Roles__FleetManager` | the same, for a fleet manager |
| `Authorization:Roles:Administrator` | `Authorization__Roles__Administrator` | the same, for an administrator |

**The host refuses to start while any of the three role values is empty.** That is deliberate: a policy
whose role is blank would match nothing, and an endpoint that nobody can reach looks the same as an
endpoint that is broken. `OperationalReader` has no value of its own — it accepts the operator role or
the fleet manager role.

No realm name, client id, role value or authority appears anywhere in code.

### Optional

| Setting | Default | What it does |
|---|---|---|
| `Database:MigrateOnStartup` | `false` | migrate while starting. Only `docker-compose.yml` sets it. A value that is neither `true` nor `false` is refused at boot rather than read as "off" |
| `Security:ClaimMapping:Preset` | `Keycloak` | or `GenericOidc`, which reads a flat `roles` claim |
| `Security:TenantClaim` | `tenant_id` | which claim names the tenant. This product is single-tenant (X-2) |
| `Security:RequireHttpsMetadata` | `true` | concerns the issuer's metadata URL; leave it on |
| `Security:AllowAnonymousHealthEndpoints` | `true` | whether `/health/*` answers without a token |
| `Gateway:TrustedProxies` | `[]` | the gateway addresses or CIDR networks allowed to set `X-Forwarded-*`. Empty means the host ignores those headers entirely |
| `Transport:RestPort` / `Transport:GrpcPort` | `8080` / `8081` | the listeners endpoints are bound to |
| `Transport:EnforcePortSeparation` | `true` | bind each endpoint to its listener. Set `false` only for a single-port TLS deployment |
| `Transport:EnableOpenApi` | Development only | the OpenAPI document |
| `Transport:EnableGrpcReflection` | Development only | gRPC server reflection |
| `Observability:*` | no export | per-signal exporter, endpoint, protocol, sampling and redaction |
| `Observability:Metrics:Prometheus:Enabled` | `false` | a protected Prometheus pull endpoint on the REST listener |

`Observability:*:Headers` carries a backend API key; it belongs in user secrets or the environment,
never in `appsettings.json`.

## Migrations

Migrations are design time. Add one with the Api as the startup project:

```bash
dotnet tool install --global dotnet-ef   # once
export ConnectionStrings__PostgreSql="Host=localhost;Port=5432;Database=fserp_fleetoperations;Username=...;Password=..."
dotnet ef migrations add <Name> \
  --project src/Fserp.FleetOperations.Infrastructure \
  --startup-project src/Fserp.FleetOperations.Api
```

Apply it:

```bash
dotnet ef database update \
  --project src/Fserp.FleetOperations.Infrastructure \
  --startup-project src/Fserp.FleetOperations.Api
```

Both commands read the database from the environment and never from `appsettings.json`, whose value is
a placeholder: a migration must name the database it changes rather than inherit one.
`src/Fserp.FleetOperations.Api/Hosting/AppDbContextDesignTimeFactory.cs` builds the context they use,
with no interceptor and no message bus; the file explains why. Review every generated migration before
applying it — a migration is code that changes production data.

A schema change belongs in the same change as the code that needs it. The four existing migrations are
in `src/Fserp.FleetOperations.Infrastructure/Persistence/Migrations/`.

**In compose**, migrations are applied by the application at startup, because `Database__MigrateOnStartup`
is set there and only there. See [With Docker Compose](#with-docker-compose).

**Audit table permissions.** `audit.entries` is append-only by convention. Grant the runtime role
nothing more:

```sql
GRANT USAGE ON SCHEMA audit TO app_runtime;
GRANT INSERT, SELECT ON audit.entries TO app_runtime;
GRANT USAGE ON SEQUENCE audit.entries_"Id"_seq TO app_runtime;
```

## Tests

Two projects. `dotnet test` without a filter runs both.

```bash
dotnet build -c Release
dotnet test -c Release --no-build
```

### Unit and host tests

```bash
dotnet test tests/Fserp.FleetOperations.UnitTests -c Release --no-build
```

They need nothing: no database, no cache, no broker, no identity provider. They cover the domain rules,
the validators, the handlers against in-memory fakes of their ports, the EF Core model, the message
texts, the module boundaries, and the host — the real `Program` pipeline hosted in memory with tokens
signed by an in-process RSA key, so `401`, `403` and `200` are measured through the real authentication
and authorization stack.

### Running the integration tests

```bash
dotnet test tests/Fserp.FleetOperations.IntegrationTests -c Release --no-build
```

They need a real PostgreSQL, because what they prove is PostgreSQL behaviour: `xmin` optimistic
concurrency, unique partial indexes, an audit row that survives the rollback that wrote it, and the
ordering and bounds of the audit query. An in-memory provider would prove none of it.

**On a Docker-capable machine** they start a `postgres:17-alpine` container through Testcontainers and
run with no further setup.

**Without Docker**, point them at a database you are willing to have migrated and written to:

```bash
export FLEETOPS_TEST_POSTGRES="Host=localhost;Port=5432;Database=fleetops_test;Username=...;Password=..."
dotnet test tests/Fserp.FleetOperations.IntegrationTests -c Release --no-build
```

When `FLEETOPS_TEST_POSTGRES` is set, no container is started and that database is used as it is. The
fixture applies this repository's own migrations to it on first use, so it must be a database you can
throw away — not a shared one and never a production one.

**When neither is available**, every integration test skips, individually, with the reason printed:

```
No PostgreSQL available: set FLEETOPS_TEST_POSTGRES or make Docker available for Testcontainers (...)
```

A skip is not a pass. An earlier environment without Docker skipped the suite; the 2026-10-06 run
completed all 92 integration tests against PostgreSQL and Redis with no skips. Check the counts in
your own run before relying on its result.

## Using the API

Every endpoint but the health probes requires `Authorization: Bearer <token>` from the configured
issuer, and a role one of the four policies accepts.

| Policy | Role required | Covers |
|---|---|---|
| `Operator` | operator | the six mission commands |
| `FleetManager` | fleet manager | vehicle and driver registration, status changes, maintenance |
| `OperationalReader` | operator **or** fleet manager | every read of vehicles, drivers and missions |
| `Administrator` | administrator | `GET /api/administration/audit-entries` and nothing else |

`Administrator` is **not** a superset role: an administrator reads the audit trail and is refused on
every operational read (AD-1).

### REST — `:8080`

| Method and path | Policy |
|---|---|
| `POST /api/fleet/vehicles` | FleetManager |
| `PUT /api/fleet/vehicles/{vehicleId}/status` | FleetManager |
| `POST /api/fleet/vehicles/{vehicleId}/maintenance/start` | FleetManager |
| `POST /api/fleet/vehicles/{vehicleId}/maintenance/complete` | FleetManager |
| `GET /api/fleet/vehicles/{vehicleId}` | OperationalReader |
| `GET /api/fleet/vehicles/available` | OperationalReader |
| `POST /api/drivers` | FleetManager |
| `PUT /api/drivers/{driverId}/status` | FleetManager |
| `GET /api/drivers/{driverId}` | OperationalReader |
| `GET /api/drivers/available` | OperationalReader |
| `POST /api/operations/missions` | Operator |
| `POST /api/operations/missions/{missionId}/schedule` | Operator |
| `POST /api/operations/missions/{missionId}/assign` | Operator |
| `POST /api/operations/missions/{missionId}/start` | Operator |
| `POST /api/operations/missions/{missionId}/complete` | Operator |
| `POST /api/operations/missions/{missionId}/cancel` | Operator |
| `GET /api/operations/missions/{missionId}` | OperationalReader |
| `GET /api/operations/missions/active` | OperationalReader |
| `GET /api/administration/audit-entries` | Administrator |
| `GET /health/live`, `/health/ready`, `/health/startup` | anonymous |

Enum members travel as names, never as numbers, in request and response bodies alike.

```bash
TOKEN="<a token from your issuer>"

# Register a vehicle.
curl -i -X POST http://localhost:8080/api/fleet/vehicles \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"plateNumber":"AB-1234","vehicleType":"Truck","capacityKg":9000}'

# The available list (cached for at most 30 s; advisory, never what Assign decides on).
curl -s http://localhost:8080/api/fleet/vehicles/available -H "Authorization: Bearer $TOKEN"

# Create, schedule and assign a mission.
curl -s -X POST http://localhost:8080/api/operations/missions \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"origin":"Tehran","destination":"Shiraz","requiredCapacityKg":800}'

curl -s -X POST http://localhost:8080/api/operations/missions/$MISSION/schedule \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"scheduledAt":"2026-10-06T07:30:00Z"}'

curl -s -X POST http://localhost:8080/api/operations/missions/$MISSION/assign \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d "{\"vehicleId\":\"$VEHICLE\",\"driverId\":\"$DRIVER\"}"

# One bounded page of active missions.
curl -s "http://localhost:8080/api/operations/missions/active?page=1&pageSize=20" \
  -H "Authorization: Bearer $TOKEN"
```

A failure is RFC 9457 `application/problem+json` carrying the error's own domain and code:
`400` for input shape, `404` for a resource that does not exist, `409` for a lost concurrency race or a
duplicate plate number, `422` for a broken business rule.

```json
{
  "type": "urn:mpcore:error:fleet:VEHICLE_UNDER_MAINTENANCE",
  "status": 422,
  "errorDomain": "fleet",
  "errorCode": "VEHICLE_UNDER_MAINTENANCE",
  "detail": "The vehicle is under maintenance."
}
```

Project failure texts are available in English and Persian. Send `Accept-Language: fa` for Persian;
English is the default and the fallback for unsupported languages. The error domain and code do not
change with the language, so clients should branch on those rather than `detail`.

#### Reading the audit trail

```bash
curl -s "http://localhost:8080/api/administration/audit-entries?module=fleet&outcome=Rejected&pageSize=50" \
  -H "Authorization: Bearer $ADMIN_TOKEN"
```

Every parameter is optional; omitting all of them reads the whole trail, newest first, a page at a time.

| Parameter | Meaning |
|---|---|
| `module` | `fleet`, `drivers`, `operations` |
| `entityType` | `Vehicle`, `Driver`, `Mission` |
| `entityId` | the affected entity's identifier |
| `actor` | the recorded actor's **subject id** |
| `correlationId` | the trace identifier, to pull one operation out of the trail |
| `category` | `EntityChange` or `BusinessAction` |
| `outcome` | `Succeeded`, `Rejected` or `Failed` |
| `from` | **inclusive** lower bound on the occurrence time, UTC |
| `to` | **exclusive** upper bound on the occurrence time, UTC |
| `page`, `pageSize` | one-based page. Absent or zero means "not chosen": page 1, 20 rows. The size is capped at 200, and the audit provider caps it again at 500 |

**`to` is exclusive.** An entry stamped exactly `to` is *not* returned. That is what lets
`[a, b)` and `[b, c)` tile the timeline without overlapping and without losing a row — and reading it as
inclusive is the likelier mistake, which is why it is said here, in the endpoint's XML documentation and
in the query's. `from` is inclusive. Giving `from` equal to or after `to` selects nothing, so it is
refused as a `400` validation failure — one violation on the `from` field, carrying the rule code
`AUDIT_RANGE_INVALID` and the message key `administration.audit_range_invalid` — rather than answered
with an empty page you would read as "nothing happened".

One row answers who, what, when, which entity and with what result:

```json
{
  "items": [
    {
      "occurredAtUtc": "2026-10-05T09:31:00+00:00",
      "actor": { "kind": "User", "subjectId": "e4b1...", "clientId": "fleet-ops-ui", "userName": "ada" },
      "module": "operations",
      "category": "BusinessAction",
      "entityType": "Mission",
      "entityId": "0192...",
      "action": "MissionAssigned",
      "outcome": "Rejected",
      "failure": { "domain": "fleet", "code": "VEHICLE_UNDER_MAINTENANCE" },
      "correlationId": "0af7651916cd43dd8448eb211c80319c"
    }
  ],
  "number": 1,
  "size": 20,
  "total": 412
}
```

`actor` carries the whole recorded snapshot because one field cannot answer "who" for every row: a
background job's name is recorded in `userName` with no subject, and an anonymous rejected attempt has
neither. The `actor` filter matches `subjectId` alone.

Reading the trail is **not** itself audited (AD-2), so looking at it does not grow it.

### gRPC — `:8081`

Cleartext HTTP/2 on a port of its own: Kestrel does not sniff the HTTP/2 preface, so a single cleartext
`Http1AndHttp2` endpoint cannot serve prior-knowledge h2c. A single-port deployment is supported only
over TLS, where ALPN negotiates; set `Transport:EnforcePortSeparation` to `false` in that case.

The gRPC surface is **reads only**, and it is a subset of REST: there is no gRPC command anywhere, no
driver service (the Drivers plan records "gRPC: none"), and no audit query (AD-3).

| Service | Method | Policy |
|---|---|---|
| `fserp.fleetoperations.fleet.v1.VehicleService` | `GetVehicle`, `GetAvailableVehicles` | OperationalReader |
| `fserp.fleetoperations.operations.v1.MissionService` | `GetMission`, `GetActiveMissions` | OperationalReader |
| `fserp.fleetoperations.v1.PlatformProbe` | `GetStatus` | authenticated |
| `grpc.health.v1.Health` | `Check` | anonymous |

Field numbers in `src/Fserp.FleetOperations.Api/Protos/` are permanent: never renumber one, never reuse
a removed one. Capacities travel as decimal strings in kilograms, because protobuf has no decimal type
and a `double` would change the value.

With [grpcurl](https://github.com/fullstorydev/grpcurl):

```bash
# Health, which is anonymous.
grpcurl -plaintext localhost:8081 grpc.health.v1.Health/Check

# Reflection: set Transport:EnableGrpcReflection=true outside Development, and send a token.
grpcurl -plaintext -H "Authorization: Bearer $TOKEN" localhost:8081 list
grpcurl -plaintext -H "Authorization: Bearer $TOKEN" localhost:8081 describe fserp.fleetoperations.fleet.v1.VehicleService

# One vehicle.
grpcurl -plaintext -H "Authorization: Bearer $TOKEN" \
  -d '{"vehicle_id":"0192...-...."}' \
  localhost:8081 fserp.fleetoperations.fleet.v1.VehicleService/GetVehicle

# The available list.
grpcurl -plaintext -H "Authorization: Bearer $TOKEN" \
  -d '{}' localhost:8081 fserp.fleetoperations.fleet.v1.VehicleService/GetAvailableVehicles

# One bounded page of active missions. Zero or absent means "not chosen", exactly as over REST.
grpcurl -plaintext -H "Authorization: Bearer $TOKEN" \
  -d '{"page":1,"page_size":20}' \
  localhost:8081 fserp.fleetoperations.operations.v1.MissionService/GetActiveMissions
```

Without reflection, point grpcurl at the `.proto` files instead:

```bash
grpcurl -plaintext -import-path src/Fserp.FleetOperations.Api/Protos \
  -proto fleet/v1/vehicles.proto \
  -H "Authorization: Bearer $TOKEN" \
  -d '{"vehicle_id":"0192...-...."}' \
  localhost:8081 fserp.fleetoperations.fleet.v1.VehicleService/GetVehicle
```

[grpcui](https://github.com/fullstorydev/grpcui) is the browser equivalent and needs reflection:
`grpcui -plaintext localhost:8081`. Swagger UI is a REST client and does not speak gRPC.

The same failures reach a gRPC caller as a status with rich error details carrying the same domain and
code: `InvalidArgument` for input shape, `NotFound`, `Aborted` for a lost race, `FailedPrecondition` for
a broken rule. Send `accept-language: fa` metadata for Persian. The translated project text is in the
rich `google.rpc.LocalizedMessage` detail, and stable domain/code are in `google.rpc.ErrorInfo`; the
native gRPC status detail remains a generic safe description.

### Description surfaces

Both are opt-in, default to Development only, and are anonymous **only** in Development. Enabled
elsewhere they exist but require a token like everything else — the flag says "expose it", not "expose
it to anyone".

- OpenAPI document: `http://localhost:8080/openapi/v1.json`
- Swagger UI: `http://localhost:8080/openapi-ui/` (Development only, never served elsewhere)
- gRPC reflection: `Transport:EnableGrpcReflection`

## Security

The host is a bearer-only OAuth 2.0 / OIDC resource server. **Login, signup, OTP, password reset and
identity-provider administration are never implemented here**, in this repository or in a future one.

- Identity comes only from the validated token, through `ICurrentActorAccessor`. A user id, tenant or
  role taken from a body, query string, route value or an arbitrary header is not identity, and a
  forwarded-identity header guard strips such headers before authentication.
- Roles reaching an authorization decision come only from the configured sources
  (`realm_access.roles` and `resource_access.*.roles` by default). A top-level `role` claim in a token
  is discarded by design: that claim type is MP Core's synthesized output, never an input.
- Every endpoint without authorization metadata is protected by the authenticated fallback. The only
  anonymous endpoints are the health probes, which return the aggregate status word and nothing else.
- Each endpoint is bound to the Kestrel listener it may be served from, by the accepting socket's local
  port — not by the `Host` header or the `:authority` pseudo-header, both of which a client or a proxy
  controls.
- `X-Forwarded-*` is honoured only from the proxies listed in `Gateway:TrustedProxies`.
- TLS is terminated at the edge gateway; the generated listeners are cleartext.
  `Security:RequireHttpsMetadata` concerns the issuer's metadata URL and stays `true` regardless.

A gateway in front does not replace any of this: signature, issuer, audience and expiry are validated
here, and business authorization — whether *this* actor may act on *that* record — can only be decided
here.

**No credential, realm URL, client secret or connection string belongs in this repository.** Not in
`appsettings.json`, not in `docker-compose.yml`, not in a commit message, not in an issue.

## Working with an AI assistant

Nothing here installs a tool or grants a permission. `CLAUDE.md` holds the project instructions; the
skill bodies live once at `.mpcore/skills/<name>/SKILL.md` and the files under `.claude/skills/` are
thin adapters that point at them. Edit the body, never an adapter.
[docs/ai-skills.md](docs/ai-skills.md) explains each skill and gives a prompt you can paste.

## Where to read next

| | |
|---|---|
| [docs/architecture.md](docs/architecture.md) | components, layers, request path, trust boundary, and the five recorded design decisions |
| [docs/plans/](docs/plans/) | one note per module: aggregates, invariants, commands, queries, decided questions |
| [docs/getting-started.md](docs/getting-started.md) | the generated quick start, in more detail |
| [docs/capabilities.md](docs/capabilities.md) | everything MP Core supports, and what is deliberately absent |
| [src/Modules/README.md](src/Modules/README.md) | the module layout and how one module makes another change |
| [OUTCOMES.md](OUTCOMES.md) | what was built, round by round |
