using Fserp.FleetOperations.Api.Hosting;
using Fserp.FleetOperations.Infrastructure;
using Fserp.FleetOperations.Infrastructure.Persistence;
using MPCore.Hosting;
using MPCore.Localization;
using MPCore.Messaging.Wolverine;
using MPCore.Observability;
using MPCore.Observability.Prometheus;
using MPCore.Security.AspNetCore;
using MPCore.Validation.FluentValidation;
using Fserp.FleetOperations.Api.Grpc.Services;
using MPCore.Transport.Grpc;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Fserp.FleetOperations.Api.Rest.Endpoints;
using MPCore.Transport.Http;
using Fserp.FleetOperations.Modules.Administration.Infrastructure;
using Fserp.FleetOperations.Modules.Administration.Resources;
using Fserp.FleetOperations.Modules.Drivers.Infrastructure;
using Fserp.FleetOperations.Modules.Drivers.Resources;
using Fserp.FleetOperations.Modules.Fleet.Infrastructure;
using Fserp.FleetOperations.Modules.Fleet.Resources;
using Fserp.FleetOperations.Modules.Operations.Infrastructure;
using Fserp.FleetOperations.Modules.Operations.Resources;
using Fserp.FleetOperations.Api.Resources;
using Fserp.FleetOperations.Api.Security;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// Per-endpoint Kestrel "Protocols" values in appsettings.json are authoritative; no protocol is
// forced globally.
// A single cleartext Http1AndHttp2 endpoint cannot serve prior-knowledge h2c HTTP/2, so the
// endpoint that carries binary RPC declares Http2 exclusively.
const TransportMode HostTransport = TransportMode.Both;
TransportEndpointGuard.Validate(builder.Configuration, HostTransport);

builder.Services.AddMPCoreFoundation(new MPCoreObservabilityOptions
{
    ServiceName = "Fserp.FleetOperations",
    ServiceNamespace = "Fserp",
    ServiceVersion = typeof(Program).Assembly.GetName().Version?.ToString(),
    EnableOtlpExporter = builder.Configuration.GetValue("Observability:EnableOtlpExporter", false),
    // Each signal has its own destination, sampling and redaction settings; see docs/architecture.md.
    Signals = builder.Configuration.GetSection("Observability").Get<MPCoreObservabilitySignals>()
});
// Prometheus pull is a REST-listener surface. It carries no anonymous metadata: the scraper must
// present a bearer token, or the deployment must confine the listener to the scraper's network.
var metricsScrapeEnabled = builder.Configuration.GetValue("Observability:Metrics:Prometheus:Enabled", false);
if (metricsScrapeEnabled)
{
    builder.Services.AddMPCorePrometheusScrape();
}
// One registration per bounded-context module, each from its own project. Every module shares the one
// AppDbContext, the single unit-of-work owner. See src/Modules/README.md.
builder.Services.AddFleetModule<AppDbContext>();
builder.Services.AddDriversModule<AppDbContext>();
builder.Services.AddOperationsModule<AppDbContext>();
builder.Services.AddAdministrationModule<AppDbContext>();

// Failures reach the caller as message keys, rendered in English or Persian: MP Core's own
// messages and each project's resource file supply both languages. See
// docs/architecture.md, "Business rules, validation and messages".
builder.Services.AddMPCoreMessageCatalog(catalog =>
{
    catalog.AddResources<FleetMessages>();
    catalog.AddResources<DriversMessages>();
    catalog.AddResources<OperationsMessages>();
    catalog.AddResources<AdministrationMessages>();
    // Host-wide failures that belong to no module (a lost concurrency race): Hosting/HostFailures.cs.
    catalog.AddResources<HostMessages>();
});
// Input validators (FluentValidation) of every handler assembly. They run before the handler.
foreach (var assembly in HandlerAssemblies.All)
{
    builder.Services.AddMPCoreValidators(assembly);
}

// Bearer-only OIDC resource server. Login, signup, OTP, password and identity-provider
// administration are Product surfaces and are never implemented here.
builder.Services.AddMPCoreBearerAuthentication(options =>
{
    options.Authority = builder.Configuration["Security:Authority"]
        ?? throw new InvalidOperationException("Security:Authority is required.");
    options.RequireHttpsMetadata = builder.Configuration.GetValue("Security:RequireHttpsMetadata", true);
    foreach (var audience in builder.Configuration.GetSection("Security:Audiences").Get<string[]>() ?? [])
    {
        options.ValidAudiences.Add(audience);
    }
});
// Claim mapping follows the identity provider. Keycloak-shaped by default (realm and client roles,
// preferred_username, azp, service-account-* convention); GenericOidc reads a flat roles claim.
builder.Services.AddMPCoreCurrentActor(mapping =>
{
    if (string.Equals(builder.Configuration["Security:ClaimMapping:Preset"], "GenericOidc", StringComparison.OrdinalIgnoreCase))
    {
        mapping.UseGenericOidc();
    }
    else
    {
        mapping.UseKeycloakDefaults();
    }
});
// The tenant, when the token names one. Business code reads ITenantContext; audit records it.
builder.Services.AddMPCoreTenancyFromClaim(builder.Configuration["Security:TenantClaim"] ?? "tenant_id");
builder.Services.AddForwardedIdentityHeaderGuard();
// X-Forwarded-* is honoured only from the proxies listed here (APISIX or another gateway). Empty
// means the host reasons from the real connection and ignores the headers entirely.
builder.Services.AddMPCoreGatewayForwarding(options =>
{
    foreach (var proxy in builder.Configuration.GetSection("Gateway:TrustedProxies").Get<string[]>() ?? [])
    {
        options.TrustedProxies.Add(proxy);
    }
});

// MP Core does not map the health probes, so it cannot enforce anonymous access to them. This host
// maps them and therefore owns the decision, applied with AllowAnonymous() where they are mapped.
var allowAnonymousHealthEndpoints =
    builder.Configuration.GetValue("Security:AllowAnonymousHealthEndpoints", true);
builder.Services.AddMPCoreAuthorization();
// The four product policies (docs/architecture.md, decision 5). Role values come from configuration,
// never from code; the host refuses to start without them.
builder.Services.AddOptions<AuthorizationRoleOptions>()
    .Bind(builder.Configuration.GetSection(AuthorizationRoleOptions.Section))
    .Validate(static roles => roles.IsComplete(), $"Every role value under {AuthorizationRoleOptions.Section} is required.")
    .ValidateOnStart();
builder.Services.AddPolicyContributor<FleetOperationsPolicyContributor>();

// Description surfaces are opt-in and default to Development only: an OpenAPI document and gRPC
// reflection describe the entire API to whoever can reach them.
var enableOpenApi = DeveloperEndpoints.IsDescriptionSurfaceEnabled(
    builder.Configuration, builder.Environment, "Transport:EnableOpenApi");
var enableGrpcReflection = DeveloperEndpoints.IsDescriptionSurfaceEnabled(
    builder.Configuration, builder.Environment, "Transport:EnableGrpcReflection");

// A description surface is only reachable without a token in Development. Enabling one explicitly in
// another environment keeps it behind the authenticated fallback: the flag says "expose it", not
// "expose it to anyone".
var anonymousDescriptionSurface = builder.Environment.IsDevelopment();

// What "alive" and "ready" mean is the same on every transport: Hosting/HostHealthChecks.cs.
builder.Services.AddHostHealthChecks();

builder.Services.AddGrpc().AddMPCoreFailureHandling(options =>
{
    options.DefaultCulture = "en";
    options.SupportedCultures.Add("fa");
});
// The empty service name is the whole host; "live" asks the process only.
builder.Services.AddGrpcHealthChecks(options =>
    options.Services.Map(HostHealthChecks.Live, static check => check.Tags.Contains(HostHealthChecks.Live)));
if (enableGrpcReflection)
{
    builder.Services.AddGrpcReflection();
}
// The gRPC twins of the two HTTP exception mappers (F-10): the same failure descriptors, from the same
// static entry points, so a unique violation and a lost xmin race answer identically on both transports.
// MP Core 0.9.3 has no AddGrpcExceptionMapper extension; the failure interceptor resolves
// IEnumerable<IGrpcExceptionMapper> from the container, so both are registered here. They hold no state,
// so a singleton is resolvable from the interceptor whatever its own lifetime.
builder.Services.AddSingleton<IGrpcExceptionMapper, UniqueViolationGrpcExceptionMapper>();
builder.Services.AddSingleton<IGrpcExceptionMapper, ConcurrencyGrpcExceptionMapper>();
builder.Services.AddMPCoreHttpFailureHandling(options =>
{
    options.DefaultCulture = "en";
    options.SupportedCultures.Add("fa");
});
builder.Services.AddMPCoreProblemDetailsSecurityResponses();
// A unique violation on an index a module declared (the plate number) is that module's 409, not a 500.
builder.Services.AddHttpExceptionMapper<UniqueViolationExceptionMapper>();
// A lost optimistic-concurrency race (xmin) on any module's aggregate is 409, not a 500.
builder.Services.AddHttpExceptionMapper<ConcurrencyExceptionMapper>();
// A body that cannot be read (a number or an unknown name for an enum, an empty body) is thrown rather
// than answered as a bare 400, so MP Core's Problem Details middleware answers it as
// 400 mpcore.http/MALFORMED_REQUEST, in every environment (ASP.NET Core throws only in Development).
builder.Services.Configure<RouteHandlerOptions>(static options => options.ThrowOnBadRequest = true);
// REST bodies name enum members (Van, Active, ...) rather than numbers; a number is refused.
builder.Services.ConfigureHttpJsonOptions(static options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));
if (enableOpenApi)
{
    builder.Services.AddOpenApi();
}

var databaseConnection = builder.Configuration.GetConnectionString("PostgreSql")
    ?? throw new InvalidOperationException("ConnectionStrings:PostgreSql is required.");
var cacheConnection = builder.Configuration.GetConnectionString("Redis")
    ?? throw new InvalidOperationException("ConnectionStrings:Redis is required for the selected cache.");
builder.Services.AddInfrastructure(databaseConnection, cacheConnection);
// AppDbContext is named here as the transaction owner, so a handler can depend on IUnitOfWork and
// still run inside the Entity Framework transaction whose commit releases its outgoing messages.
builder.Host.UseMPCoreWolverine<AppDbContext>(
    new WolverineFoundationOptions
    {
        ServiceName = "Fserp.FleetOperations",
        PersistenceConnectionString = databaseConnection,
        PersistenceSchemaName = "wolverine",
        // This project's own handlers are discovered from here, in addition to HandlerAssemblies.All.
        ApplicationAssembly = typeof(Program).Assembly
    },
    options =>
    {
        // Handlers are discovered only in the assemblies this host names. Nothing is scanned
        // implicitly and no catch-all policy exists; see Hosting/HandlerAssemblies.cs.
        options.DiscoverHandlersIn(HandlerAssemblies.All);
        // A message whose validators fail never reaches its handler; the caller receives MP Core's
        // validation failure with one violation per field.
        options.UseMPCoreFluentValidation();
    });

var app = builder.Build();

// Schema changes are a deployment step here, not a start-up side effect: this returns immediately
// unless Database:MigrateOnStartup is explicitly true, which only docker-compose.yml sets (L-31).
// Hosting/DatabaseStartup.cs holds the gate and the reasoning.
await app.ApplyMigrationsIfRequestedAsync();

// ADR-007 pipeline order. UseAuthentication always precedes UseAuthorization, and both follow
// UseRouting so the authenticated fallback policy sees resolved endpoint metadata.
// Forwarded headers come first, so everything after it sees the scheme and client the gateway saw.
app.UseMPCoreGatewayForwarding();
app.UseMPCoreProblemDetails();
app.UseMPCoreRequestContext();
app.UseForwardedIdentityHeaderGuard();
if (enableOpenApi && anonymousDescriptionSurface)
{
    // The UI is a static shell that a browser navigates to, so it cannot carry a bearer token, and
    // the authenticated fallback policy applies to middleware-served content as well as to mapped
    // endpoints. It is therefore mounted ahead of authentication and only in Development. Outside
    // Development it is not served at all; the document endpoint remains and stays protected.
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/openapi/v1.json", "Fserp.FleetOperations v1");
        options.RoutePrefix = "openapi-ui";
    });
}

app.UseRouting();
// Runs after routing so the resolved endpoint's listener binding can be checked, and before
// authentication so a misrouted call is refused without evaluating any credential.
app.UseTransportPortSeparation();
app.UseAuthentication();
app.UseAuthorization();

var grpcProbeEndpoint = app.MapGrpcService<PlatformProbeService>();
// The Fleet read surface over gRPC. Same queries as REST, same policy as the REST reads (decision 5).
var grpcVehicleEndpoint = app.MapGrpcService<VehicleService>()
    .RequireAuthorization(FleetOperationsPolicies.OperationalReader);
// The Operations read surface over gRPC. Same queries as REST, same policy as the REST reads (decision 5).
var grpcMissionEndpoint = app.MapGrpcService<MissionService>()
    .RequireAuthorization(FleetOperationsPolicies.OperationalReader);
var grpcHealthEndpoint = app.MapGrpcHealthChecksService();
if (allowAnonymousHealthEndpoints)
{
    grpcHealthEndpoint.AllowAnonymous();
}
IEndpointConventionBuilder? grpcReflection = null;
if (enableGrpcReflection)
{
    // Reflection lets grpcui and grpcurl discover services without a local .proto copy. Outside
    // Development it stays behind the authenticated fallback, so enabling it on a shared host does not
    // hand the service inventory to an anonymous caller.
    grpcReflection = app.MapGrpcReflectionService();
    if (anonymousDescriptionSurface)
    {
        grpcReflection.AllowAnonymous();
    }
}

IEndpointConventionBuilder? openApiDocument = null;
if (enableOpenApi)
{
    // The document describes the whole REST surface. Anonymous only in Development; when enabled
    // elsewhere it exists but requires a token like every other endpoint.
    openApiDocument = app.MapOpenApi();
    if (anonymousDescriptionSurface)
    {
        openApiDocument.AllowAnonymous();
    }
}

var restProbeEndpoints = app.MapPlatformProbeEndpoints();
var vehicleEndpoints = app.MapVehicleEndpoints();
// The Drivers surface. REST only: docs/plans/drivers.md records "gRPC: none" for both driver queries.
var driverEndpoints = app.MapDriverEndpoints();
// The Operations surface: six mission commands under Operator, two reads under OperationalReader.
var missionEndpoints = app.MapMissionEndpoints();
// The Administration surface: one audit read under Administrator. REST only (AD-3).
var auditEntryEndpoints = app.MapAuditEntryEndpoints();
var livenessEndpoint = app.MapHealthChecks(
    "/health/live",
    new HealthCheckOptions
    {
        Predicate = static check => check.Tags.Contains(HostHealthChecks.Live),
        ResponseWriter = WriteAggregateStatusAsync
    });
var readinessEndpoint = app.MapHealthChecks(
    "/health/ready",
    new HealthCheckOptions { ResponseWriter = WriteAggregateStatusAsync });
var startupEndpoint = app.MapHealthChecks(
    "/health/startup",
    new HealthCheckOptions { ResponseWriter = WriteAggregateStatusAsync });
if (allowAnonymousHealthEndpoints)
{
    livenessEndpoint.AllowAnonymous();
    readinessEndpoint.AllowAnonymous();
    startupEndpoint.AllowAnonymous();
}

IEndpointConventionBuilder? metricsScrape = null;
if (metricsScrapeEnabled)
{
    metricsScrape = app.MapMPCorePrometheusScrape(app.Configuration.GetValue("Observability:Metrics:Prometheus:Path", "/metrics")!);
}

// Endpoints are bound to the Kestrel listener they may be served from, never to the client-supplied
// Host header. TransportEndpointGuard has already proved both ports match real listeners.
if (app.Configuration.GetValue("Transport:EnforcePortSeparation", true))
{
    var restPort = app.Configuration.GetValue("Transport:RestPort", 8080);
    var grpcPort = app.Configuration.GetValue("Transport:GrpcPort", 8081);
    grpcProbeEndpoint.RequireListenerPort(grpcPort);
    grpcVehicleEndpoint.RequireListenerPort(grpcPort);
    grpcMissionEndpoint.RequireListenerPort(grpcPort);
    grpcHealthEndpoint.RequireListenerPort(grpcPort);
    restProbeEndpoints.RequireListenerPort(restPort);
    vehicleEndpoints.RequireListenerPort(restPort);
    driverEndpoints.RequireListenerPort(restPort);
    missionEndpoints.RequireListenerPort(restPort);
    auditEntryEndpoints.RequireListenerPort(restPort);
    livenessEndpoint.RequireListenerPort(restPort);
    readinessEndpoint.RequireListenerPort(restPort);
    startupEndpoint.RequireListenerPort(restPort);
    openApiDocument?.RequireListenerPort(restPort);
    metricsScrape?.RequireListenerPort(restPort);
    grpcReflection?.RequireListenerPort(grpcPort);
}

await app.RunAsync();

// Health responses expose the aggregate status word only. Check names, dependency hosts, durations
// and exception text are never disclosed anonymously.
static Task WriteAggregateStatusAsync(HttpContext context, HealthReport report)
{
    context.Response.ContentType = "text/plain; charset=utf-8";
    return context.Response.WriteAsync(report.Status.ToString());
}

public partial class Program;
