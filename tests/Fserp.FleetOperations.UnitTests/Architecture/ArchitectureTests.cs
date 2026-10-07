using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Fserp.FleetOperations.Modules.Fleet.Domain;
using Fserp.FleetOperations.Modules.Fleet.Resources;
using MPCore.Application.Messaging;
using MPCore.Domain.Rules;
using MPCore.Persistence.Abstractions;

namespace Fserp.FleetOperations.UnitTests.Architecture;

/// <summary>Locates the repository from the test output directory.</summary>
internal static class Repository
{
    public static string Root { get; } = FindRoot();

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Fserp.FleetOperations.Backend.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Repository root not found above " + AppContext.BaseDirectory);
    }

    public static string ModulesDirectory => Path.Combine(Root, "src", "Modules");

    public static IEnumerable<string> ModuleProjects() =>
        Directory.EnumerateFiles(ModulesDirectory, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    public static IReadOnlyList<string> ProjectReferencesOf(string project) =>
        XDocument.Load(project)
            .Descendants("ProjectReference")
            .Select(reference => Path.GetFileNameWithoutExtension(reference.Attribute("Include")!.Value.Replace('\\', '/')))
            .ToList();
}

/// <summary>
/// docs/architecture.md, "Layers and dependency direction" and "Module map": a module never references
/// another module's main project, the host or the host's Infrastructure; the allowed Contracts references
/// are exactly the ones the module map records. Read from the project files, because the compiler drops
/// an unused reference from the assembly metadata.
/// </summary>
public sealed class ModuleBoundaryTests
{
    private const string Prefix = "Fserp.FleetOperations.Modules.";

    public static TheoryData<string, string[]> AllowedReferences() => new()
    {
        { "Fleet", ["Fleet.Contracts"] },
        { "Fleet.Contracts", [] },
        { "Drivers", ["Drivers.Contracts", "Fleet.Contracts"] },
        { "Drivers.Contracts", ["Fleet.Contracts"] },
        { "Operations", ["Fleet.Contracts", "Drivers.Contracts"] },
        { "Administration", [] },
    };

    [Theory]
    [MemberData(nameof(AllowedReferences))]
    public void A_module_project_references_only_the_contracts_the_module_map_allows(string module, string[] allowed)
    {
        var project = Assert.Single(Repository.ModuleProjects(), path => Path.GetFileNameWithoutExtension(path) == Prefix + module);

        var references = Repository.ProjectReferencesOf(project);

        Assert.All(references, reference => Assert.Contains(reference, allowed.Select(name => Prefix + name)));
    }

    [Fact]
    public void Every_module_project_is_covered_by_the_boundary_rule()
    {
        var covered = AllowedReferences().Select(row => Prefix + (string)row[0]).ToHashSet(StringComparer.Ordinal);

        Assert.All(Repository.ModuleProjects(), project => Assert.Contains(Path.GetFileNameWithoutExtension(project), covered));
    }

    [Fact]
    public void No_module_references_the_host_or_its_infrastructure()
    {
        foreach (var project in Repository.ModuleProjects())
        {
            var references = Repository.ProjectReferencesOf(project);
            Assert.DoesNotContain("Fserp.FleetOperations.Api", references);
            Assert.DoesNotContain("Fserp.FleetOperations.Infrastructure", references);
        }
    }

    [Fact]
    public void No_module_main_project_is_referenced_by_another_module()
    {
        foreach (var project in Repository.ModuleProjects())
        {
            var self = Path.GetFileNameWithoutExtension(project);
            var mainProjectsOfOthers = Repository.ProjectReferencesOf(project)
                .Where(reference => reference.StartsWith(Prefix, StringComparison.Ordinal) && !reference.EndsWith(".Contracts", StringComparison.Ordinal));

            Assert.DoesNotContain(mainProjectsOfOthers, reference => reference != self);
        }
    }
}

/// <summary>
/// docs/architecture.md and the verify skill, step 3: the <c>Domain</c> and <c>Application</c> folders of
/// every module use no Entity Framework, ASP.NET, broker, gRPC or database-driver types. Two checks: the
/// <c>using</c> directives of the source files, and the member signatures of the compiled Fleet types.
/// </summary>
public sealed class LayerTests
{
    private static readonly string[] ForbiddenNamespaces =
    [
        "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "Wolverine", "JasperFx", "Npgsql",
        "StackExchange.Redis", "Grpc", "Google.Protobuf", "RabbitMQ", "Confluent.Kafka", "MassTransit",
    ];

    private static readonly Regex UsingDirective = new(@"^\s*(?:global\s+)?using\s+(?:static\s+)?(?:\w+\s*=\s*)?(?<ns>[\w\.]+)\s*;", RegexOptions.Compiled);

    public static TheoryData<string> LayerFolders() => ["Domain", "Application"];

    private static IEnumerable<string> SourcesIn(string folder) =>
        Directory.EnumerateDirectories(Repository.ModulesDirectory, folder, SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(path => Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories));

    [Theory]
    [MemberData(nameof(LayerFolders))]
    public void The_layer_folder_imports_no_infrastructure_namespace(string folder)
    {
        var offending = SourcesIn(folder)
            .SelectMany(file => File.ReadLines(file).Select(line => (file, match: UsingDirective.Match(line))))
            .Where(item => item.match.Success)
            .Select(item => (item.file, ns: item.match.Groups["ns"].Value))
            .Where(item => ForbiddenNamespaces.Any(forbidden => item.ns == forbidden || item.ns.StartsWith(forbidden + ".", StringComparison.Ordinal))
                || item.ns.Contains(".Infrastructure", StringComparison.Ordinal))
            .Select(item => $"{Path.GetRelativePath(Repository.Root, item.file)}: using {item.ns}")
            .ToList();

        Assert.True(offending.Count == 0, string.Join(Environment.NewLine, offending));
    }

    [Fact]
    public void The_domain_folder_imports_nothing_from_the_application_folder()
    {
        var offending = SourcesIn("Domain")
            .SelectMany(file => File.ReadLines(file).Select(line => (file, match: UsingDirective.Match(line))))
            .Where(item => item.match.Success && item.match.Groups["ns"].Value.Contains(".Application", StringComparison.Ordinal)
                && item.match.Groups["ns"].Value.StartsWith("Fserp.", StringComparison.Ordinal))
            .Select(item => Path.GetRelativePath(Repository.Root, item.file))
            .ToList();

        Assert.True(offending.Count == 0, string.Join(Environment.NewLine, offending));
    }

    [Fact]
    public void The_layer_folders_were_found()
    {
        // Guards the two tests above against passing vacuously on a moved folder.
        Assert.Contains(SourcesIn("Domain"), file => file.EndsWith("Vehicle.cs", StringComparison.Ordinal));
        Assert.Contains(SourcesIn("Application"), file => file.EndsWith("RegisterVehicle.cs", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(".Domain")]
    [InlineData(".Application")]
    public void No_Fleet_domain_or_application_signature_mentions_an_infrastructure_type(string layer)
    {
        var assembly = Fserp.FleetOperations.Modules.Fleet.AssemblyReference.Assembly;
        var types = assembly.GetTypes().Where(type => type.Namespace?.Contains("Modules.Fleet" + layer, StringComparison.Ordinal) == true).ToList();
        Assert.NotEmpty(types);

        var offending = types
            .SelectMany(type => SignatureTypes(type).Select(used => (type, used)))
            .Where(item => IsForbidden(item.used))
            .Select(item => $"{item.type.FullName} uses {item.used.FullName}")
            .Distinct()
            .ToList();

        Assert.True(offending.Count == 0, string.Join(Environment.NewLine, offending));
    }

    private static bool IsForbidden(Type type)
    {
        var assemblyName = type.Assembly.GetName().Name ?? string.Empty;
        return ForbiddenNamespaces.Any(forbidden => assemblyName.StartsWith(forbidden, StringComparison.Ordinal))
            || (type.Namespace?.Contains(".Infrastructure", StringComparison.Ordinal) ?? false)
                && type.Namespace!.StartsWith("Fserp.", StringComparison.Ordinal);
    }

    private static IEnumerable<Type> SignatureTypes(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var direct = new List<Type>();
        if (type.BaseType is not null)
        {
            direct.Add(type.BaseType);
        }

        direct.AddRange(type.GetInterfaces());
        direct.AddRange(type.GetFields(all).Select(field => field.FieldType));
        direct.AddRange(type.GetProperties(all).Select(property => property.PropertyType));
        foreach (var method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
        {
            direct.AddRange(method.GetParameters().Select(parameter => parameter.ParameterType));
            if (method is MethodInfo info)
            {
                direct.Add(info.ReturnType);
            }
        }

        return direct.SelectMany(Expand);
    }

    private static IEnumerable<Type> Expand(Type type)
    {
        if (type.HasElementType)
        {
            foreach (var inner in Expand(type.GetElementType()!))
            {
                yield return inner;
            }

            yield break;
        }

        yield return type;
        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments().SelectMany(Expand))
            {
                yield return argument;
            }
        }
    }
}

/// <summary>
/// The verify skill, step 3: every message key the code uses has a text in each supported language.
/// The host supports <c>en</c> and <c>fa</c>. The neutral English resource is checked here; the
/// five catalogs' exact English/Persian key parity is checked by ProjectMessageCultureTests.
/// </summary>
public sealed class FleetMessageCoverageTests
{
    private static readonly ResourceManager Texts = new(typeof(FleetMessages));

    private static IEnumerable<string> DeclaredCodes() =>
        typeof(FleetErrors)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string) && field.Name != nameof(FleetErrors.Domain))
            .Select(field => (string)field.GetRawConstantValue()!);

    public static TheoryData<string> EveryCode()
    {
        var data = new TheoryData<string>();
        foreach (var code in DeclaredCodes())
        {
            data.Add(code);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryCode))]
    public void Every_code_declared_in_FleetErrors_has_a_text(string code)
    {
        var key = FleetErrors.MessageKey(code);

        Assert.False(string.IsNullOrWhiteSpace(Texts.GetString(key, CultureInfo.InvariantCulture)), $"No text for {key}.");
    }

    [Fact]
    public void Every_business_rule_of_the_module_uses_a_declared_code_its_own_domain_and_a_key_with_a_text()
    {
        var rules = Fserp.FleetOperations.Modules.Fleet.AssemblyReference.Assembly.GetTypes()
            .Where(type => typeof(BusinessRule).IsAssignableFrom(type) && !type.IsAbstract)
            .ToList();
        Assert.NotEmpty(rules);
        var declared = DeclaredCodes().ToHashSet(StringComparer.Ordinal);

        foreach (var type in rules)
        {
            var constructor = type.GetConstructors().Single();
            var rule = (BusinessRule)constructor.Invoke(constructor.GetParameters().Select(parameter => parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null).ToArray());

            Assert.Equal(FleetErrors.Domain, rule.ErrorDomain);
            Assert.Contains(rule.Code, declared);
            Assert.Equal(FleetErrors.MessageKey(rule.Code), rule.MessageKey);
            Assert.False(string.IsNullOrWhiteSpace(Texts.GetString(rule.MessageKey, CultureInfo.InvariantCulture)), $"No text for {rule.MessageKey}.");
        }
    }
}

/// <summary>
/// docs/architecture.md, "How a command executes" and "Reading", and CLAUDE.md, "Identity comes from the
/// validated token": the shape of every Fleet message and handler, checked by reflection so a later
/// handler is held to it too.
/// </summary>
public sealed class FleetHandlerShapeTests
{
    private static readonly Assembly Fleet = Fserp.FleetOperations.Modules.Fleet.AssemblyReference.Assembly;

    private static IEnumerable<Type> Messages(Type marker) =>
        Fleet.GetTypes().Where(type => type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == marker));

    private static MethodInfo HandlerOf(Type message) =>
        Assert.Single(
            Fleet.GetTypes().SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static)),
            method => method.Name == "Handle" && method.GetParameters().FirstOrDefault()?.ParameterType == message);

    [Fact]
    public void The_module_has_its_four_commands_and_two_queries()
    {
        // The census of the Fleet message surface after rounds 1 to 4 (L-6). CommitToMission and
        // ReleaseFromMission are deliberately absent: they are not bus messages (docs/plans/fleet.md).
        Assert.Equal(
            ["ChangeVehicleStatus", "CompleteMaintenance", "RegisterVehicle", "StartMaintenance"],
            Messages(typeof(ICommand<>)).Select(type => type.Name).Order());
        Assert.Equal(
            ["GetAvailableVehicles", "GetVehicle"],
            Messages(typeof(IQuery<>)).Select(type => type.Name).Order());
    }

    [Fact]
    public void No_command_or_query_carries_an_actor()
    {
        var actorLike = new Regex("actor|user|subject|caller|tenant|role", RegexOptions.IgnoreCase);
        foreach (var message in Messages(typeof(ICommand<>)).Concat(Messages(typeof(IQuery<>))))
        {
            Assert.DoesNotContain(message.GetProperties(), property => actorLike.IsMatch(property.Name));
        }
    }

    [Fact]
    public void Every_query_handler_declares_no_unit_of_work_and_no_repository()
    {
        foreach (var query in Messages(typeof(IQuery<>)))
        {
            var parameters = HandlerOf(query).GetParameters().Select(parameter => parameter.ParameterType).ToList();
            Assert.DoesNotContain(typeof(IUnitOfWork), parameters);
            Assert.DoesNotContain(parameters, type => type.IsGenericType ? false : type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRepository<,>)));
        }
    }

    [Fact]
    public void Every_command_handler_declares_the_unit_of_work_and_takes_only_interfaces()
    {
        foreach (var command in Messages(typeof(ICommand<>)))
        {
            var ports = HandlerOf(command).GetParameters().Skip(1).Select(parameter => parameter.ParameterType).ToList();
            Assert.Contains(typeof(IUnitOfWork), ports);
            Assert.All(ports.Where(type => type != typeof(CancellationToken)), type => Assert.True(type.IsInterface, $"{command.Name} handler takes {type.Name}, which is not a port."));
        }
    }
}
