# Administration: plan note

Status: approved. Challenge sections 10 and 13.

**Responsibility.** Makes the business audit trail readable by authorized users. It owns no business
state and no aggregate: the trail is written by the other modules through `IBusinessAuditRecorder` and
the persistence-layer interceptor, and stored by MP Core in `audit.entries`. Administration only reads it,
through `IAuditQuery`, behind the `Administrator` policy.

Out of scope here: users, roles, realms, login. Identity and role administration belong to the identity
provider; this host is a bearer-only resource server.

Project: `src/Modules/Administration/Fserp.FleetOperations.Modules.Administration`. The `Domain/` folder
stays empty. No Contracts project. Error domain `administration`.

## Aggregates and invariants

None. An append-only trail has no invariant this module could break; nothing here writes.

## Commands

None.

## Queries

| Query | REST | gRPC |
|---|---|---|
| `GetAuditEntries(filter, page)` -> `Page<AuditEntryView>` | `GET /api/administration/audit-entries` | none (AD-3) |

Query parameters, mapped one to one onto `AuditQueryFilter` and `AuditPageRequest`: `module`, `entityType`,
`entityId`, `actor` (subject id), `correlationId`, `category` (`EntityChange`, `BusinessAction`),
`outcome` (`Succeeded`, `Rejected`, `Failed`), `from`, `to` (UTC, `to` exclusive), `page`, `pageSize`
(capped by the provider). Newest first.

The handler takes `IAuditQuery` (MP Core port) and maps each `AuditEntry` to an `AuditEntryView` owned by
this module, so the REST contract does not change if the package type does. The view answers the five
questions of challenge section 10: who (`actor`), what (`action` or `category`), when (`occurredAtUtc`),
which entity (`module`, `entityType`, `entityId`), result (`outcome`, `failure.domain`, `failure.code`),
plus `correlationId` to join with traces.

Validation: `from < to` when both given; enums parsed against their allowlist; `pageSize` within bounds.

## Domain events

None.

## Authorization

`Administrator` policy on the endpoint, and the handler checks nothing more (no per-record ownership in
the challenge). Tests: no token `401`; Operator or Fleet Manager token `403`; Administrator `200`.

## Business audit

Reading the trail is not itself audited (AD-2).

## Cache candidates

None. The trail must be current when an administrator investigates.

## Dependencies

MP Core `MPCore.Audit.Abstractions` only. No other module, no Contracts project.

## Decisions on former open questions

- **AD-1 Decided (owner, via A-1).** Administrator reads audit only; it is not a superset of the other
  roles and is not an `OperationalReader`.
- **AD-2 Decided (default).** Reading the audit trail is not recorded as a business action.
- **AD-3 Decided (default).** No gRPC audit query.

### Decided (lead), rounds 10 and 11

- **L-29.** The handler declares `IAuditQuery` and `CancellationToken` and nothing else. No unit of work,
  no repository, no read model of its own.
- **L-30.** `to` is exclusive and `from` inclusive, as this note states. Said again in the README and in
  the parameter's XML doc, because an inclusive reading is the likelier caller mistake.
- **L-31.** The migrate-on-startup gate defaults to **off**; only `docker-compose.yml` turns it on. A
  deployment that forgets the variable does not migrate, and does not silently migrate either.
- **L-32. Pushback accepted — `AddAdministrationModule` registers nothing.** My brief told the developer
  to register the port there. That was wrong and the developer proved it: `IAuditQuery` is already
  registered by `AddMPCoreAudit<AppDbContext>` in `Infrastructure.DependencyInjection`, because
  `businessAudit` is `postgresql`. Registering it again would force the module to reference
  `MPCore.Audit.EntityFrameworkCore.PostgreSql` — a persistence provider inside a module that owns no
  persistence, which `ModuleBoundaryTests` exists to prevent. The handler needs no registration either;
  Wolverine discovers it from the assemblies in `HandlerAssemblies.All`. Proved against the real host
  container rather than asserted in a comment.
- **L-33. The actor is a nested object, not a scalar subject id.** The plan's "who (`actor`)" is served
  by `actor { kind, subjectId, clientId, userName }`, because MP Core's `AuditActor.SystemActor` leaves
  `SubjectId` null and carries the name in `UserName`: a scalar `subjectId` would answer "who" with
  `null` for every background change and every anonymous rejected attempt. The `actor` **filter** still
  maps to `ActorSubjectId` alone, exactly as this note specifies.
- **L-34. No `pageSize` validator rule.** The note asks for "`pageSize` within bounds", but
  `AuditPageRequest` normalizes itself and the provider caps again, so a FluentValidation rule over it
  could not fail for any caller input. A rule that cannot fail reads as protection and is not. The bound
  is proved where it is actually enforced, including a host test for `?pageSize=100000`.

### Known limitations carried out of round 10 — for the owner

- **The view omits `changes`.** An `EntityChange` row therefore says *that* an entity changed but not
  *what* changed; `changes` holds the before/after pairs the audit policy already allowlisted and masked.
  For an investigator this is the likeliest next request. It was not added because this note enumerates
  the view's fields and does not name it: adding it is a REST contract change and the owner's call.
- **The view omits `reason`**, the one human-readable sentence on a rejected attempt. Same reasoning.
- **Query-string enums accept their numeric values** (`?category=0`), while request bodies do not
  (F-9's `JsonStringEnumConverter(allowIntegerValues: false)` applies to bodies only). This is a
  host-wide asymmetry between bodies and query strings, not specific to this endpoint. A nonsense value
  is still `400 mpcore.http/MALFORMED_REQUEST`, measured rather than assumed.
- **At the end of round 10, this repository had no `.gitignore`**, while `docker-compose.yml` documented
  a local `.env` that must never be committed. A `.gitignore` has since been added; review it and the
  staged files before the first commit.
