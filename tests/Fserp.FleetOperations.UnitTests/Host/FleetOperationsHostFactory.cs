using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Wolverine;

namespace Fserp.FleetOperations.UnitTests.Host;

/// <summary>
/// The real <c>Program</c> pipeline (authentication, the four policies, Problem Details, the exception
/// mappers, the vehicle endpoints) hosted in memory, with three substitutions and nothing else:
/// <list type="bullet">
/// <item>the OIDC issuer is an in-process RSA key: the bearer handler validates signature, issuer,
/// audience and lifetime exactly as configured, but against static metadata instead of discovery;</item>
/// <item>the hosted services are removed, so Wolverine does not start and no PostgreSQL is contacted;</item>
/// <item><see cref="IMessageBus"/> is a recording stand-in, so an endpoint's outcome can be chosen and
/// a request that never reached the bus can be proved.</item>
/// </list>
/// The handlers themselves are not run here; they are covered by the handler tests.
/// </summary>
public sealed class FleetOperationsHostFactory : WebApplicationFactory<Program>
{
    // Test-only values. None of them names a real issuer, realm, audience or role.
    public const string Authority = "https://issuer.test.invalid/realms/fleet-ops-test";
    public const string Audience = "fleet-ops-test-api";
    public const string OperatorRole = "test-operator";
    public const string FleetManagerRole = "test-fleet-manager";
    public const string AdministratorRole = "test-administrator";

    private readonly RsaSecurityKey _signingKey = new(RSA.Create(2048)) { KeyId = "fleet-ops-test-key" };

    /// <summary>The bus the endpoints send to.</summary>
    public RecordingBus Bus { get; } = DispatchProxy.Create<IMessageBus, RecordingBus>() as RecordingBus
        ?? throw new InvalidOperationException("Proxy creation failed.");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Security:Authority", Authority);
        builder.UseSetting("Security:Audiences:0", Audience);
        builder.UseSetting("Security:RequireHttpsMetadata", "true");
        builder.UseSetting("Security:ClaimMapping:Preset", "Keycloak");
        builder.UseSetting("Authorization:Roles:Operator", OperatorRole);
        builder.UseSetting("Authorization:Roles:FleetManager", FleetManagerRole);
        builder.UseSetting("Authorization:Roles:Administrator", AdministratorRole);
        builder.UseSetting("ConnectionStrings:PostgreSql", "Host=never-contacted.invalid;Database=none;Username=none;Password=none");
        builder.UseSetting("ConnectionStrings:Redis", "never-contacted.invalid:6379,abortConnect=false");

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IMessageBus>();
            services.AddSingleton<IMessageBus>(_ => (IMessageBus)(object)Bus);

            var metadata = new OpenIdConnectConfiguration { Issuer = Authority };
            metadata.SigningKeys.Add(_signingKey);
            services.PostConfigureAll<JwtBearerOptions>(options =>
            {
                options.Configuration = metadata;
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata);
            });
        });
    }

    /// <summary>A token as the configured issuer would sign it, with the given realm roles.</summary>
    public string Token(params string[] realmRoles) => Sign(new TokenShape { RealmRoles = realmRoles });

    /// <summary>A token with full control over its shape, for the negative cases.</summary>
    public string Sign(TokenShape shape, SecurityKey? key = null)
    {
        var now = DateTime.UtcNow;
        var claims = new Dictionary<string, object>
        {
            ["sub"] = "test-subject-" + Guid.NewGuid().ToString("N"),
            ["preferred_username"] = "test-user",
            ["realm_access"] = new Dictionary<string, object> { ["roles"] = shape.RealmRoles },
        };
        foreach (var (name, value) in shape.ExtraClaims)
        {
            claims[name] = value;
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = shape.Issuer ?? Authority,
            Audience = shape.Audience ?? Audience,
            IssuedAt = now.AddMinutes(-2),
            NotBefore = shape.Expired ? now.AddMinutes(-30) : now.AddMinutes(-2),
            Expires = shape.Expired ? now.AddMinutes(-10) : now.AddMinutes(10),
            Claims = claims,
            SigningCredentials = new SigningCredentials(key ?? _signingKey, SecurityAlgorithms.RsaSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    /// <summary>A client, optionally carrying a bearer token.</summary>
    public HttpClient Client(string? token = null)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }
}

/// <summary>The parts of a test token a negative case varies.</summary>
public sealed class TokenShape
{
    public string[] RealmRoles { get; init; } = [];

    public string? Issuer { get; init; }

    public string? Audience { get; init; }

    public bool Expired { get; init; }

    public Dictionary<string, object> ExtraClaims { get; init; } = [];
}

/// <summary>
/// An <see cref="IMessageBus"/> that records every message sent with <c>InvokeAsync&lt;T&gt;</c> and
/// answers with <see cref="Respond"/>: a returned value is the handler's result, a thrown exception is
/// what the handler or the middleware's commit threw. Every other member is refused.
/// </summary>
public class RecordingBus : DispatchProxy
{
    private readonly ConcurrentQueue<object> _sent = new();

    public IReadOnlyCollection<object> Sent => _sent;

    public Func<object, object?> Respond { get; set; } = _ => throw new InvalidOperationException("No response configured.");

    public void Reset(Func<object, object?> respond)
    {
        _sent.Clear();
        Respond = respond;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        if (targetMethod.Name != nameof(IMessageBus.InvokeAsync) || !targetMethod.IsGenericMethod || args is null || args.Length == 0)
        {
            throw new NotSupportedException($"RecordingBus does not support {targetMethod}.");
        }

        var message = args[0]!;
        _sent.Enqueue(message);
        var resultType = targetMethod.GetGenericArguments()[0];
        try
        {
            var value = Respond(message);
            return FromResult.MakeGenericMethod(resultType).Invoke(null, [value]);
        }
        catch (Exception exception)
        {
            return FromException.MakeGenericMethod(resultType).Invoke(null, [exception]);
        }
    }

    private static readonly MethodInfo FromResult = typeof(Task).GetMethod(nameof(Task.FromResult))!;

    private static readonly MethodInfo FromException = typeof(Task)
        .GetMethods()
        .Single(method => method.Name == nameof(Task.FromException) && method.IsGenericMethodDefinition);
}
