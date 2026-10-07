using System.Globalization;
using System.Resources;
using Fserp.FleetOperations.Modules.Fleet.Domain;
using Fserp.FleetOperations.Modules.Fleet.Resources;

namespace Fserp.FleetOperations.UnitTests.Fleet;

public sealed class FleetMessagesTests
{
    public static TheoryData<string> Codes() =>
    [
        FleetErrors.PlateNumberRequired,
        FleetErrors.PlateNumberTooLong,
        FleetErrors.CapacityMustBePositive,
        FleetErrors.PlateNumberAlreadyRegistered,
        FleetErrors.HasMissionCommitment,
        FleetErrors.NotFound,
    ];

    [Theory]
    [MemberData(nameof(Codes))]
    public void Every_code_of_the_slice_has_an_English_text(string code)
    {
        // The catalog reads the resource named after the marker type, as MP Core's AddResources<T> does.
        var resources = new ResourceManager(typeof(FleetMessages));

        var text = resources.GetString(FleetErrors.MessageKey(code), CultureInfo.InvariantCulture);

        Assert.False(string.IsNullOrWhiteSpace(text), $"No text for {FleetErrors.MessageKey(code)}.");
    }

    [Fact]
    public void Keys_follow_the_module_convention()
    {
        Assert.Equal("fleet.vehicle_has_mission_commitment", FleetErrors.MessageKey(FleetErrors.HasMissionCommitment));
    }
}
