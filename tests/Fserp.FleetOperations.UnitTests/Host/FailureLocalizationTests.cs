using System.Collections;
using System.Globalization;
using System.Net;
using System.Resources;
using System.Text.RegularExpressions;
using Fserp.FleetOperations.Api.Resources;
using Fserp.FleetOperations.Modules.Administration.Resources;
using Fserp.FleetOperations.Modules.Drivers.Resources;
using Fserp.FleetOperations.Modules.Fleet.Application;
using Fserp.FleetOperations.Modules.Fleet.Application.Views;
using Fserp.FleetOperations.Modules.Fleet.Resources;
using Fserp.FleetOperations.Modules.Operations.Resources;
using Grpc.Core;
using Grpc.Net.Client;
using Google.Rpc;
using MPCore.Application.Results;
using static Fserp.FleetOperations.UnitTests.Host.FleetOperationsHostFactory;
using Proto = Fserp.FleetOperations.Api.Grpc.Fleet.V1;

namespace Fserp.FleetOperations.UnitTests.Host;

public sealed class ProjectMessageCultureTests
{
    public static TheoryData<Type> Catalogs => new()
    {
        typeof(FleetMessages),
        typeof(DriversMessages),
        typeof(OperationsMessages),
        typeof(AdministrationMessages),
        typeof(HostMessages),
    };

    [Theory]
    [MemberData(nameof(Catalogs))]
    public void Every_project_message_has_distinct_nonblank_English_and_Persian_text(Type marker)
    {
        var resources = new ResourceManager(marker);
        var english = Entries(resources, CultureInfo.InvariantCulture);
        var persian = Entries(resources, CultureInfo.GetCultureInfo("fa"));

        Assert.NotEmpty(english);
        Assert.Equal(english.Keys.Order(StringComparer.Ordinal), persian.Keys.Order(StringComparer.Ordinal));
        foreach (var key in english.Keys)
        {
            Assert.False(string.IsNullOrWhiteSpace(english[key]), $"Missing English text for {key}.");
            Assert.False(string.IsNullOrWhiteSpace(persian[key]), $"Missing Persian text for {key}.");
            Assert.NotEqual(english[key], persian[key]);
            Assert.Equal(Placeholders(english[key]), Placeholders(persian[key]));
        }
    }

    private static Dictionary<string, string> Entries(ResourceManager resources, CultureInfo culture)
    {
        using var set = resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
        Assert.NotNull(set);
        return set.Cast<DictionaryEntry>().ToDictionary(
            entry => (string)entry.Key,
            entry => Assert.IsType<string>(entry.Value),
            StringComparer.Ordinal);
    }

    private static string[] Placeholders(string text) =>
        Regex.Matches(text, @"\{[^{}]+\}")
            .Select(match => match.Value)
            .Order(StringComparer.Ordinal)
            .ToArray();
}

public sealed class FailureLocalizationTests : IClassFixture<FleetOperationsHostFactory>
{
    private readonly FleetOperationsHostFactory _host;

    public FailureLocalizationTests(FleetOperationsHostFactory host)
    {
        _host = host;
        _host.Bus.Reset(static _ => Result<VehicleView>.FromFailure(FleetFailures.VehicleNotFound()));
    }

    [Theory]
    [InlineData("en", "en")]
    [InlineData("fa", "fa")]
    [InlineData("fr", "en")]
    [InlineData(null, "en")]
    public async Task REST_negotiates_project_failure_and_keeps_identity_and_status(string? requested, string expected)
    {
        using var client = _host.Client(_host.Token(OperatorRole));
        using var request = VehicleHttp.Request("get");
        if (requested is not null)
        {
            request.Headers.TryAddWithoutValidation("Accept-Language", requested);
        }

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await VehicleHttp.ProblemOf(response);
        Assert.Equal("fleet", problem.GetProperty("errorDomain").GetString());
        Assert.Equal("VEHICLE_NOT_FOUND", problem.GetProperty("errorCode").GetString());
        Assert.Equal(ExpectedText(expected), problem.GetProperty("detail").GetString());
    }

    [Theory]
    [InlineData("en", "en")]
    [InlineData("fa", "fa")]
    [InlineData("fr", "en")]
    [InlineData(null, "en")]
    public async Task gRPC_negotiates_project_failure_and_keeps_status(string? requested, string expected)
    {
        using var channel = GrpcChannel.ForAddress(
            _host.Server.BaseAddress,
            new GrpcChannelOptions
            {
                HttpHandler = _host.Server.CreateHandler(),
                Credentials = ChannelCredentials.Insecure,
            });
        var client = new Proto.VehicleService.VehicleServiceClient(channel);
        var headers = new Metadata
        {
            { "Authorization", "Bearer " + _host.Token(OperatorRole) },
        };
        if (requested is not null)
        {
            headers.Add("accept-language", requested);
        }

        var exception = await Assert.ThrowsAsync<RpcException>(() => client.GetVehicleAsync(
            new Proto.GetVehicleRequest { VehicleId = Guid.CreateVersion7().ToString() },
            headers).ResponseAsync);

        Assert.Equal(StatusCode.NotFound, exception.StatusCode);
        var richStatus = Assert.IsType<Google.Rpc.Status>(exception.GetRpcStatus());
        var localized = Assert.Single(richStatus.Details, detail => detail.Is(LocalizedMessage.Descriptor))
            .Unpack<LocalizedMessage>();
        Assert.Equal(ExpectedText(expected), localized.Message);
        var error = Assert.Single(richStatus.Details, detail => detail.Is(ErrorInfo.Descriptor))
            .Unpack<ErrorInfo>();
        Assert.Equal("fleet", error.Domain);
        Assert.Equal("VEHICLE_NOT_FOUND", error.Reason);
    }

    private static string ExpectedText(string culture) =>
        new ResourceManager(typeof(FleetMessages)).GetString(
            "fleet.vehicle_not_found",
            CultureInfo.GetCultureInfo(culture))!;
}
