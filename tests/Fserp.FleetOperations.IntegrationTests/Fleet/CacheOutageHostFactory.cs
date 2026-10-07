using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Fserp.FleetOperations.IntegrationTests.Fleet;

/// <summary>
/// The real <c>Program</c> pipeline over the real dependencies: the real PostgreSQL of
/// <see cref="PostgreSqlFixture"/>, the real Wolverine runtime with its PostgreSQL message store, the
/// real hybrid cache over a real Redis, the real endpoints, policies and Problem Details.
/// </summary>
/// <remarks>
/// <para>
/// Two things are substituted, and only two:
/// </para>
/// <list type="bullet">
/// <item>the OIDC issuer is an in-process RSA key, so the bearer handler validates signature, issuer,
/// audience and lifetime exactly as configured but against static metadata instead of discovery — this
/// host is bearer-only and never issues a token, so there is nothing else to stand in for an issuer;</item>
/// <item>an <see cref="ILoggerProvider"/> is added to the composition, so the test asserts on log
/// records rather than on whatever a console happened to render.</item>
/// </list>
/// <para>
/// Everything that decides the outcome under test — the endpoint, the bus, the query handler, the cache
/// adapter, the read model, the connection to Redis — is the production one.
/// </para>
/// </remarks>
/// <param name="postgreSqlConnectionString">The migrated test database; also Wolverine's message store.</param>
/// <param name="redisConnectionString">
/// What the cache connects to. The outage test points this at <see cref="RedisOutageProxy"/> rather than
/// at Redis directly, which is what lets the outage be induced.
/// </param>
internal sealed class CacheOutageHostFactory(string postgreSqlConnectionString, string redisConnectionString)
    : WebApplicationFactory<Program>
{
    // Test-only values. None of them names a real issuer, realm, audience or role.
    public const string Authority = "https://issuer.test.invalid/realms/fleet-ops-cache-gate";
    public const string Audience = "fleet-ops-cache-gate-api";
    public const string OperatorRole = "test-operator";
    public const string FleetManagerRole = "test-fleet-manager";
    public const string AdministratorRole = "test-administrator";

    private readonly RsaSecurityKey _signingKey = new(RSA.Create(2048)) { KeyId = "fleet-ops-cache-gate-key" };

    /// <summary>Everything the host logged.</summary>
    public CapturingLoggerProvider Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Testing");
        builder.UseSetting("Security:Authority", Authority);
        builder.UseSetting("Security:Audiences:0", Audience);
        builder.UseSetting("Security:RequireHttpsMetadata", "true");
        builder.UseSetting("Security:ClaimMapping:Preset", "Keycloak");
        builder.UseSetting("Authorization:Roles:Operator", OperatorRole);
        builder.UseSetting("Authorization:Roles:FleetManager", FleetManagerRole);
        builder.UseSetting("Authorization:Roles:Administrator", AdministratorRole);
        builder.UseSetting("ConnectionStrings:PostgreSql", postgreSqlConnectionString);
        builder.UseSetting("ConnectionStrings:Redis", redisConnectionString);
        // The fixture migrated the schema already; start-up migration stays the deployment step it is.
        builder.UseSetting("Database:MigrateOnStartup", "false");

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ILoggerProvider>(Logs);

            var metadata = new OpenIdConnectConfiguration { Issuer = Authority };
            metadata.SigningKeys.Add(_signingKey);
            services.PostConfigureAll<JwtBearerOptions>(options =>
            {
                options.Configuration = metadata;
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata);
            });
        });
    }

    /// <summary>A client carrying a token the configured issuer would have signed, with the given roles.</summary>
    /// <param name="realmRoles">The realm roles the token carries.</param>
    public HttpClient Client(params string[] realmRoles)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(realmRoles));
        return client;
    }

    private string Token(params string[] realmRoles)
    {
        var now = DateTime.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Authority,
            Audience = Audience,
            IssuedAt = now.AddMinutes(-2),
            NotBefore = now.AddMinutes(-2),
            Expires = now.AddMinutes(30),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = "cache-gate-" + Guid.NewGuid().ToString("N"),
                ["preferred_username"] = "cache-gate",
                ["realm_access"] = new Dictionary<string, object> { ["roles"] = realmRoles },
            },
            SigningCredentials = new SigningCredentials(_signingKey, SecurityAlgorithms.RsaSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
