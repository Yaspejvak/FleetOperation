using System.Text.Json;
using Fserp.FleetOperations.Api.Hosting;
using Fserp.FleetOperations.UnitTests.Architecture;
using Microsoft.Extensions.Configuration;

namespace Fserp.FleetOperations.UnitTests.Host;

/// <summary>
/// The migrate-on-startup gate (lead decision L-31). The default is off, and only a deliberate, parseable
/// "true" opens it — a deployment that forgets the variable does not migrate, and does not silently
/// migrate either.
/// </summary>
public sealed class DatabaseStartupTests
{
    private static IConfiguration Configuration(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(setting => new KeyValuePair<string, string?>(setting.Key, setting.Value)))
            .Build();

    [Fact]
    public void With_no_configuration_at_all_the_host_does_not_migrate()
    {
        Assert.False(DatabaseStartup.MigratesOnStartup(Configuration()));
    }

    [Theory]
    [InlineData("false")]
    [InlineData("False")]
    [InlineData(" false ")]
    public void An_explicit_false_leaves_the_gate_shut(string value)
    {
        Assert.False(DatabaseStartup.MigratesOnStartup(Configuration((DatabaseStartup.MigrateOnStartupKey, value))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_variable_declared_with_no_value_is_not_set_and_takes_the_default(string? value)
    {
        // Found below the rule meant to prevent it. Api/Hosting/DatabaseStartup.cs originally read the
        // gate with configuration.GetValue<bool>(key, false), whose documented "default" applies only to
        // an *absent* key: an empty one throws InvalidOperationException from the binder. So
        // "Database__MigrateOnStartup=" in an environment file or a deployment manifest — a variable
        // declared and left blank, which is one stray keystroke away in YAML — failed the entire host at
        // boot instead of not migrating. L-31 says a deployment that forgets the variable does not
        // migrate; crashing is not "does not migrate".
        Assert.False(DatabaseStartup.MigratesOnStartup(Configuration((DatabaseStartup.MigrateOnStartupKey, value))));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData(" true ")]
    public void Only_an_explicit_true_opens_it(string value)
    {
        Assert.True(DatabaseStartup.MigratesOnStartup(Configuration((DatabaseStartup.MigrateOnStartupKey, value))));
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("treu")]
    public void A_value_that_is_neither_true_nor_false_is_refused_rather_than_read_as_off(string value)
    {
        // The other half of the same decision. Someone writing "yes" meant to open the gate; answering
        // that with a silent "off" and a stack that later cannot find its tables helps nobody. Blank is
        // "not set"; a wrong word is a mistake, and it is named.
        var failure = Assert.Throws<InvalidOperationException>(() =>
            DatabaseStartup.MigratesOnStartup(Configuration((DatabaseStartup.MigrateOnStartupKey, value))));

        Assert.Contains(DatabaseStartup.MigrateOnStartupKey, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_environment_variable_form_is_the_one_compose_sets()
    {
        // docker-compose.yml sets Database__MigrateOnStartup=true on the application service and nothing
        // else in this repository sets it. The double underscore is how the environment provider spells
        // the colon, so this is the same key.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>("Database:MigrateOnStartup", "true")])
            .Build();

        Assert.Equal("Database:MigrateOnStartup", DatabaseStartup.MigrateOnStartupKey);
        Assert.True(DatabaseStartup.MigratesOnStartup(configuration));
    }

    [Fact]
    public void The_hosts_own_settings_file_leaves_it_off()
    {
        // The default host path is unchanged: dotnet run, dotnet test and every deployment that does not
        // set the variable start without touching the schema.
        var path = Path.Combine(Repository.Root, "src", "Fserp.FleetOperations.Api", "appsettings.json");
        using var document = JsonDocument.Parse(
            File.ReadAllText(path),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        Assert.True(document.RootElement.TryGetProperty("Database", out var database));
        Assert.False(database.GetProperty("MigrateOnStartup").GetBoolean());
    }

    private static IEnumerable<string> ConfigurationFiles() =>
        Directory
            .EnumerateFiles(Repository.Root, "*", SearchOption.AllDirectories)
            .Where(file => Path.GetExtension(file) is ".json" or ".yml" or ".yaml"
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    [Fact]
    public void Exactly_two_configuration_files_mention_the_gate()
    {
        // A grep with an assertion on it. appsettings.json declares it false for every host path;
        // docker-compose.yml sets it for the throwaway stack. A third file would be a third opinion.
        var mentioning = ConfigurationFiles()
            .Where(file => File.ReadAllText(file).Contains("MigrateOnStartup", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Order()
            .ToList();

        Assert.Equal(["appsettings.json", "docker-compose.yml"], mentioning);
    }

    [Fact]
    public void Only_the_compose_file_turns_it_on_in_this_repository()
    {
        var enabling = ConfigurationFiles()
            .Where(file => File.ReadLines(file).Any(line =>
                line.Contains("MigrateOnStartup", StringComparison.Ordinal)
                && line.Contains("true", StringComparison.OrdinalIgnoreCase)))
            .Select(Path.GetFileName)
            .Order()
            .ToList();

        Assert.Equal(["docker-compose.yml"], enabling);
    }
}
