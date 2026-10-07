using System.Net;
using System.Text.Json;
using Fserp.FleetOperations.Api.Hosting;
using Fserp.FleetOperations.Api.Rest.Endpoints;
using Fserp.FleetOperations.Api.Security;
using Fserp.FleetOperations.Modules.Administration.Application;
using Fserp.FleetOperations.Modules.Administration.Application.Queries;
using Fserp.FleetOperations.Modules.Administration.Application.Views;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MPCore.Application.Querying;
using MPCore.Audit;
using MPCore.Localization;
using Wolverine;
using static Fserp.FleetOperations.UnitTests.Host.FleetOperationsHostFactory;

namespace Fserp.FleetOperations.UnitTests.Host;

/// <summary>Requests and sample values shared by the Administration host tests.</summary>
internal static class AuditHttp
{
    public const string Path = "/api/administration/audit-entries";

    /// <summary>An <c>OperationalReader</c> endpoint, used to prove Administrator is not a superset role.</summary>
    public const string OperationalReaderPath = "/api/fleet/vehicles/01920000-0000-7000-8000-000000000001";

    public static Page<AuditEntryView> SamplePage(params AuditEntryView[] items) =>
        new(items, 1, 20, items.Length);

    public static AuditEntryView SampleEntry() =>
        new(
            new DateTimeOffset(2026, 10, 5, 9, 30, 0, TimeSpan.Zero),
            new AuditEntryActorView(AuditEntryActorKind.User, "subject-7", "fleet-ops-ui", "ada"),
            "fleet",
            AuditEntryCategory.BusinessAction,
            "Vehicle",
            "01920000-0000-7000-8000-000000000001",
            "VehicleRegistered",
            AuditEntryOutcome.Succeeded,
            null,
            "0af7651916cd43dd8448eb211c80319c");

    public static AuditEntryView RejectedEntry() =>
        new(
            new DateTimeOffset(2026, 10, 5, 9, 31, 0, TimeSpan.Zero),
            new AuditEntryActorView(AuditEntryActorKind.Anonymous, null, null, null),
            "operations",
            AuditEntryCategory.BusinessAction,
            "Mission",
            "01920000-0000-7000-8000-0000000000aa",
            "MissionAssigned",
            AuditEntryOutcome.Rejected,
            new AuditEntryFailureView("fleet", "VEHICLE_UNDER_MAINTENANCE"),
            "cafe");
}

/// <summary>
/// The audit read through the real host pipeline with real signed bearer tokens, and the one thing AD-1
/// actually means: <c>Administrator</c> is its own role, not a bigger one. Four cases on this endpoint
/// (no token, Operator, Fleet Manager, Administrator) and the mirror case — an Administrator token on an
/// <c>OperationalReader</c> endpoint — which is what "not a superset" has to be proved by.
/// </summary>
public sealed class AuditEntryEndpointAuthorizationTests : IClassFixture<FleetOperationsHostFactory>
{
    private readonly FleetOperationsHostFactory _host;

    public AuditEntryEndpointAuthorizationTests(FleetOperationsHostFactory host)
    {
        _host = host;
        _host.Bus.Reset(static message => message switch
        {
            GetAuditEntries => (object)AuditHttp.SamplePage(AuditHttp.SampleEntry()),
            _ => throw new InvalidOperationException("Only the audit query is expected here."),
        });
    }

    [Fact]
    public async Task No_token_is_401_and_never_reaches_the_bus()
    {
        var response = await _host.Client().GetAsync(AuditHttp.Path);

        await VehicleHttp.AssertProblem(response, HttpStatusCode.Unauthorized, "mpcore.security", "UNAUTHENTICATED");
        Assert.Empty(_host.Bus.Sent);
    }

    [Theory]
    [InlineData(OperatorRole)]
    [InlineData(FleetManagerRole)]
    public async Task An_operator_or_a_fleet_manager_is_403_and_never_reaches_the_bus(string role)
    {
        // AD-1 and decision 5: only the administrator role reads the trail. Neither operational role is
        // promoted into it by being the one that generated most of its rows.
        var response = await _host.Client(_host.Token(role)).GetAsync(AuditHttp.Path);

        await VehicleHttp.AssertProblem(response, HttpStatusCode.Forbidden, "mpcore.security", "FORBIDDEN");
        Assert.Empty(_host.Bus.Sent);
    }

    [Fact]
    public async Task A_valid_token_with_no_role_at_all_is_403()
    {
        var response = await _host.Client(_host.Token()).GetAsync(AuditHttp.Path);

        await VehicleHttp.AssertProblem(response, HttpStatusCode.Forbidden, "mpcore.security", "FORBIDDEN");
        Assert.Empty(_host.Bus.Sent);
    }

    [Fact]
    public async Task An_administrator_is_200_and_the_query_reaches_the_bus()
    {
        var response = await _host.Client(_host.Token(AdministratorRole)).GetAsync(AuditHttp.Path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.IsType<GetAuditEntries>(Assert.Single(_host.Bus.Sent));
    }

    [Fact]
    public async Task An_administrator_is_403_on_an_operational_read()
    {
        // The other half of AD-1, and the half that actually carries the meaning: a role that may read the
        // audit trail is not thereby allowed to read the fleet. Without this, "not a superset" is only a
        // sentence in a plan note.
        var response = await _host.Client(_host.Token(AdministratorRole)).GetAsync(AuditHttp.OperationalReaderPath);

        await VehicleHttp.AssertProblem(response, HttpStatusCode.Forbidden, "mpcore.security", "FORBIDDEN");
        Assert.Empty(_host.Bus.Sent);
    }

    [Fact]
    public async Task Identity_headers_do_not_raise_an_operator_to_administrator()
    {
        // CLAUDE.md: identity never comes from an arbitrary header, and neither does a role.
        var request = new HttpRequestMessage(HttpMethod.Get, AuditHttp.Path);
        request.Headers.Add("X-Forwarded-User", "someone-else");
        request.Headers.Add("X-Forwarded-Roles", AdministratorRole);

        var response = await _host.Client(_host.Token(OperatorRole)).SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_host.Bus.Sent);
    }
}

/// <summary>
/// The REST contract of the audit read: which query parameter becomes which member of the query, what
/// "not chosen" means, and what one page looks like on the wire.
/// </summary>
public sealed class AuditEntryEndpointContractTests : IClassFixture<FleetOperationsHostFactory>
{
    private readonly FleetOperationsHostFactory _host;
    private readonly HttpClient _administrator;

    public AuditEntryEndpointContractTests(FleetOperationsHostFactory host)
    {
        _host = host;
        _host.Bus.Reset(static _ => AuditHttp.SamplePage());
        _administrator = host.Client(host.Token(AdministratorRole));
    }

    private async Task<GetAuditEntries> SentFor(string query)
    {
        var response = await _administrator.GetAsync(AuditHttp.Path + query);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<GetAuditEntries>(Assert.Single(_host.Bus.Sent));
    }

    [Fact]
    public async Task Every_query_parameter_the_plan_names_reaches_the_query()
    {
        var sent = await SentFor(
            "?module=fleet&entityType=Vehicle&entityId=v-1&actor=subject-7&correlationId=trace-9"
            + "&category=BusinessAction&outcome=Rejected"
            + "&from=2026-10-01T00:00:00Z&to=2026-10-02T00:00:00Z&page=3&pageSize=50");

        Assert.Equal("fleet", sent.Module);
        Assert.Equal("Vehicle", sent.EntityType);
        Assert.Equal("v-1", sent.EntityId);
        Assert.Equal("subject-7", sent.ActorSubjectId);
        Assert.Equal("trace-9", sent.CorrelationId);
        Assert.Equal(AuditEntryCategory.BusinessAction, sent.Category);
        Assert.Equal(AuditEntryOutcome.Rejected, sent.Outcome);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), sent.From);
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero), sent.To);
        Assert.Equal(3, sent.Page.Number);
        Assert.Equal(50, sent.Page.Size);
    }

    [Fact]
    public async Task A_request_with_no_parameter_filters_on_nothing_and_takes_the_first_page()
    {
        var sent = await SentFor(string.Empty);

        Assert.Null(sent.Module);
        Assert.Null(sent.EntityType);
        Assert.Null(sent.EntityId);
        Assert.Null(sent.ActorSubjectId);
        Assert.Null(sent.CorrelationId);
        Assert.Null(sent.Category);
        Assert.Null(sent.Outcome);
        Assert.Null(sent.From);
        Assert.Null(sent.To);
        Assert.Equal(1, sent.Page.Number);
        Assert.Equal(PageRequest.DefaultSize, sent.Page.Size);
    }

    [Theory]
    [InlineData("?page=0&pageSize=0")]
    [InlineData("?pageSize=0")]
    public async Task A_zero_page_or_size_is_not_chosen_rather_than_a_page_of_nothing(string query)
    {
        // The parity bug round 10 inherited as a standard: ?pageSize=0 must mean "I did not choose", the
        // same thing an absent parameter and an absent proto field mean. PageRequests.From is the one
        // place that decides it, and this endpoint uses it like every other paged read.
        var sent = await SentFor(query);

        Assert.Equal(1, sent.Page.Number);
        Assert.Equal(PageRequest.DefaultSize, sent.Page.Size);
    }

    [Fact]
    public async Task A_page_size_beyond_the_maximum_is_clamped_before_it_leaves_the_endpoint()
    {
        var sent = await SentFor("?pageSize=100000");

        Assert.Equal(PageRequest.MaximumSize, sent.Page.Size);
    }

    [Fact]
    public async Task The_actor_filter_comes_from_the_query_string_and_never_from_the_caller()
    {
        // The query's ActorSubjectId is a filter over recorded actors, not identity. A request that names
        // no actor must send none, whatever identity-shaped headers it carries.
        var request = new HttpRequestMessage(HttpMethod.Get, AuditHttp.Path);
        request.Headers.Add("X-Forwarded-User", "someone-else");

        var response = await _administrator.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(Assert.IsType<GetAuditEntries>(Assert.Single(_host.Bus.Sent)).ActorSubjectId);
    }

    [Theory]
    [InlineData("?category=Nonsense")]
    [InlineData("?outcome=Nonsense")]
    [InlineData("?from=not-a-date")]
    [InlineData("?page=many")]
    public async Task A_parameter_that_cannot_be_read_is_400_and_never_reaches_the_bus(string query)
    {
        var response = await _administrator.GetAsync(AuditHttp.Path + query);

        await VehicleHttp.AssertProblem(response, HttpStatusCode.BadRequest, "mpcore.http", "MALFORMED_REQUEST");
        Assert.Empty(_host.Bus.Sent);
    }

    [Fact]
    public async Task One_page_on_the_wire_names_its_enum_members_and_nests_the_failure()
    {
        _host.Bus.Reset(static _ => new Page<AuditEntryView>(
            [AuditHttp.SampleEntry(), AuditHttp.RejectedEntry()], 2, 25, 412));

        var response = await _administrator.GetAsync(AuditHttp.Path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, body.RootElement.GetProperty("number").GetInt32());
        Assert.Equal(25, body.RootElement.GetProperty("size").GetInt32());
        Assert.Equal(412, body.RootElement.GetProperty("total").GetInt64());

        var rows = body.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);

        var first = rows[0];
        Assert.Equal("fleet", first.GetProperty("module").GetString());
        Assert.Equal("BusinessAction", first.GetProperty("category").GetString());
        Assert.Equal("Succeeded", first.GetProperty("outcome").GetString());
        Assert.Equal("VehicleRegistered", first.GetProperty("action").GetString());
        Assert.Equal("Vehicle", first.GetProperty("entityType").GetString());
        Assert.Equal("0af7651916cd43dd8448eb211c80319c", first.GetProperty("correlationId").GetString());
        Assert.Equal("User", first.GetProperty("actor").GetProperty("kind").GetString());
        Assert.Equal("subject-7", first.GetProperty("actor").GetProperty("subjectId").GetString());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("failure").ValueKind);

        var second = rows[1];
        Assert.Equal("Rejected", second.GetProperty("outcome").GetString());
        Assert.Equal("fleet", second.GetProperty("failure").GetProperty("domain").GetString());
        Assert.Equal("VEHICLE_UNDER_MAINTENANCE", second.GetProperty("failure").GetProperty("code").GetString());
        Assert.Equal("Anonymous", second.GetProperty("actor").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task The_wire_shape_carries_nothing_the_plan_did_not_name()
    {
        // A census of the row's fields. The stored entry also has TenantId, OperationId, Reason, Changes
        // and Metadata; the plan's field list names none of them, so none is projected. Adding one is a
        // contract change, and this test is what makes that deliberate.
        _host.Bus.Reset(static _ => AuditHttp.SamplePage(AuditHttp.SampleEntry()));

        var response = await _administrator.GetAsync(AuditHttp.Path);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var row = Assert.Single(body.RootElement.GetProperty("items").EnumerateArray());

        Assert.Equal(
            ["action", "actor", "category", "correlationId", "entityId", "entityType", "failure", "module", "occurredAtUtc", "outcome"],
            row.EnumerateObject().Select(property => property.Name).Order());
    }

    [Fact]
    public async Task An_empty_page_is_200_with_an_empty_item_list_and_not_404()
    {
        var response = await _administrator.GetAsync(AuditHttp.Path + "?module=nothing-wrote-this");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Empty(body.RootElement.GetProperty("items").EnumerateArray());
    }
}

/// <summary>
/// The REST surface of the Administration module as the routing system sees it, and the composition the
/// module depends on: the port it reads through, the assembly its handler is discovered from, and the
/// resource file its one message key is rendered from.
/// </summary>
public sealed class AuditEntryEndpointMetadataTests : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly IReadOnlyList<RouteEndpoint> _endpoints;

    public AuditEntryEndpointMetadataTests()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton<IMessageBus>(static _ => throw new InvalidOperationException("Not used."));
        _app = builder.Build();
        _app.MapAuditEntryEndpoints();
        _endpoints = ((IEndpointRouteBuilder)_app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToList();
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    [Fact]
    public void The_module_maps_exactly_one_endpoint_behind_the_administrator_policy()
    {
        // The census of the Administration REST surface. AD-3 records no gRPC audit query, so there is no
        // proto and no service to census on the other transport.
        var endpoint = Assert.Single(_endpoints);

        Assert.Equal("/api/administration/audit-entries", endpoint.RoutePattern.RawText);
        Assert.Equal(["GET"], endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods);
        var authorization = Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
        Assert.Equal(FleetOperationsPolicies.Administrator, authorization.Policy);
        Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
    }

    [Fact]
    public void The_route_hangs_below_the_modules_one_prefix()
    {
        Assert.All(_endpoints, endpoint =>
            Assert.StartsWith("/api/administration", endpoint.RoutePattern.RawText!, StringComparison.Ordinal));
    }

    [Fact]
    public void The_read_is_a_GET_and_therefore_safe()
    {
        // A read with a consequence would be two messages. This one sends a query and nothing else.
        Assert.All(_endpoints, endpoint =>
            Assert.Equal(["GET"], endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods));
    }
}

/// <summary>
/// What <c>AddAdministrationModule</c> deliberately does not register, proved against the real host
/// container rather than asserted in a comment.
/// </summary>
public sealed class AdministrationCompositionTests : IClassFixture<FleetOperationsHostFactory>
{
    private readonly FleetOperationsHostFactory _host;

    public AdministrationCompositionTests(FleetOperationsHostFactory host) => _host = host;

    [Fact]
    public void The_audit_read_port_is_already_registered_by_the_host_composition()
    {
        // AddMPCoreAudit<AppDbContext>() in AddInfrastructure registers IAuditQuery, because businessAudit
        // is postgresql for this repository. The Administration module therefore registers no adapter of
        // its own; registering one would need the module to reference the persistence provider package.
        using var scope = _host.Services.CreateScope();

        var port = scope.ServiceProvider.GetRequiredService<IAuditQuery>();

        Assert.StartsWith("EntityFrameworkAuditQuery", port.GetType().Name, StringComparison.Ordinal);
    }

    [Fact]
    public void The_modules_assembly_is_one_the_host_discovers_handlers_in()
    {
        // Without this line in Hosting/HandlerAssemblies.cs the handler and the validator exist and never
        // run, however complete the module looks.
        Assert.Contains(
            Fserp.FleetOperations.Modules.Administration.AssemblyReference.Assembly,
            HandlerAssemblies.All);
    }

    [Fact]
    public void The_modules_resource_file_is_in_the_hosts_message_catalog()
    {
        // The validator's one message key has to render as a sentence rather than reach the caller as a
        // bare key, which is only true if AdministrationMessages is registered in Program.cs.
        var catalog = _host.Services.GetRequiredService<IMessageCatalog>();

        Assert.True(
            catalog.IsKnownKey(AdministrationErrors.MessageKey(AdministrationErrors.AuditRangeInvalid)),
            "administration.audit_range_invalid is not in the host's message catalog.");
    }
}
